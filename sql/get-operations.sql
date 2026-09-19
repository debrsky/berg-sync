/*
    Этот вариант использует hstore (O(1) доступ) для скорости и отдельный
    FIFO-массив для корректного зачета предоплаты, что решает проблему
    с медленным линейным поиском и избегает накладных расходов SQL-движка.
    --
    сделано gemini.google.com

    Скорость 10 с против 27 с с JSONB-версией.

    Резервы оптимизации -- вероятно, только распараллелить по id_seller.
*/

-- Предполагается, что расширение hstore уже установлено в вашей базе данных:
-- CREATE EXTENSION hstore;

BEGIN;
DROP FUNCTION IF EXISTS bergapp.get_operations(integer, integer);
COMMIT;

BEGIN;
CREATE OR REPLACE FUNCTION bergapp.get_operations(
    p_id_payer integer DEFAULT NULL::integer,
    p_id_seller integer DEFAULT NULL::integer)
    RETURNS TABLE(
        op_seq_num integer,
        id_seller integer,
        id_payer integer,
        op_date date,
        op_date_ts timestamp without time zone,
        op_type integer,
        id_invoice integer,
        inv_amount numeric,
        op_amount numeric,

        balance_before numeric,
        charge_amount numeric,
        payment_amount numeric,
        balance_after numeric,

        prepayment_before numeric,
        prepayment_added numeric,
        prepayment_applied numeric,
        prepayment_after numeric,

        inv_debt_before numeric,
        inv_debt_after numeric,

        debt_invoices_before jsonb,
        debt_invoices_after jsonb
    )
    LANGUAGE 'plpgsql'
    COST 100
    VOLATILE PARALLEL UNSAFE
    ROWS 1000
AS $BODY$
DECLARE
    rec RECORD;

    -- 1. HSTORE для быстрого доступа O(1): 'invoice_id' => 'debt_amount'
    v_invoice_debt_map hstore := ''::hstore;

    -- 2. Простой массив для порядка FIFO (First-In, First-Out)
    v_fifo_invoices integer[] := '{}'::integer[];

    v_op_seq_num integer := 0;

    v_id_invoice integer;
    v_op_type integer;
    v_op_amount numeric;

    v_balance_before numeric := 0;
    v_charge_amount numeric := 0;
    v_payment_amount numeric := 0;
    v_balance_after numeric := 0;

    v_prepayment_before numeric := 0;
    v_prepayment_added numeric := 0;
    v_prepayment_applied numeric := 0;
    v_prepayment_after numeric := 0;

    v_inv_debt_before numeric := 0;
    v_inv_debt_after numeric := 0;

    v_invoice_id_text text; -- Переменная для ключа hstore
    prev_id_seller integer;
    prev_id_payer integer;
BEGIN
    FOR rec IN
        -- Исходный CTE operations
        SELECT * FROM (
            WITH invoices AS (
                SELECT inv."ID" AS f_event_id, inv."ID" AS f_id_invoice, inv."ID_Boss" AS f_id_seller, app."ID_CustomerPay" AS f_id_payer, inv."Date"::date AS f_op_date, inv."Date" AS f_op_date_ts, ROUND(inv."Cost"::numeric, 2) AS f_amount, ROUND(inv."Cost"::numeric, 2) AS f_inv_amount
                FROM bergauto."XInvoices" inv JOIN bergauto."Applications" app ON app."ID" = inv."ID_Application"
                WHERE inv."Nomer" IS NOT NULL AND inv."Nomer" > 0 AND (p_id_payer IS NULL OR app."ID_CustomerPay" = p_id_payer) AND (p_id_seller IS NULL OR inv."ID_Boss" = p_id_seller)
            ), payments AS (
                SELECT invp."ID" AS f_event_id, invp."ID_XInvoice" AS f_id_invoice, inv."ID_Boss" AS f_id_seller, app."ID_CustomerPay" AS f_id_payer, invp."Date"::date AS f_op_date, invp."Date" AS f_op_date_ts, ROUND(invp."Cost"::numeric, 2) AS f_amount_paid, ROUND(inv."Cost"::numeric, 2) AS f_inv_amount
                FROM bergauto."XInvoicePays" invp JOIN bergauto."XInvoices" inv ON inv."ID" = invp."ID_XInvoice" JOIN bergauto."Applications" app ON app."ID" = inv."ID_Application"
                WHERE inv."Nomer" IS NOT NULL AND inv."Nomer" > 0 AND (p_id_payer IS NULL OR app."ID_CustomerPay" = p_id_payer) AND (p_id_seller IS NULL OR inv."ID_Boss" = p_id_seller)
            ), operations AS (
                SELECT f_event_id AS event_id, f_id_seller AS id_seller, f_id_payer AS id_payer, f_id_invoice AS id_invoice, f_op_date AS op_date, f_op_date_ts as op_date_ts, 1 AS op_type, 10 AS ord, f_inv_amount AS inv_amount, f_amount AS op_amount FROM invoices
                UNION ALL
                SELECT f_event_id AS event_id, f_id_seller AS id_seller, f_id_payer AS id_payer, f_id_invoice AS id_invoice, f_op_date AS op_date, f_op_date_ts as op_date_ts, 2 AS op_type, 20 AS ord, f_inv_amount AS inv_amount, f_amount_paid AS op_amount FROM payments
            )
            SELECT * FROM operations
        ) AS op
        ORDER BY id_seller, id_payer, op_date, ord, op_date_ts, event_id
    LOOP
        -- Сброс при смене контрагента
        IF prev_id_seller IS DISTINCT FROM rec.id_seller OR prev_id_payer IS DISTINCT FROM rec.id_payer THEN
            v_op_seq_num := 0;
            v_balance_after := 0;
            v_prepayment_after := 0;

            -- Сброс in-memory структур (HSTORE и FIFO)
            v_invoice_debt_map := ''::hstore;
            v_fifo_invoices := '{}'::integer[];
        END IF;

        v_op_seq_num := v_op_seq_num + 1;
        v_balance_before := v_balance_after;
        v_prepayment_before := v_prepayment_after;

        v_id_invoice := rec.id_invoice;
        v_op_type := rec.op_type;
        v_op_amount := COALESCE(rec.op_amount, 0);
        v_invoice_id_text := v_id_invoice::text; -- Ключ для HSTORE

        -- Быстрое получение долга по ключу инвойса (O(1))
        -- Преобразуем текстовое значение HSTORE обратно в numeric
        v_inv_debt_before := COALESCE((v_invoice_debt_map -> v_invoice_id_text)::numeric, 0);

        v_charge_amount := 0;
        v_payment_amount := 0;
        v_prepayment_added := 0;
        v_prepayment_applied := 0;

        IF v_op_type = 1 THEN
            v_charge_amount := v_op_amount;
        ELSIF v_op_type = 2 THEN
            v_payment_amount := LEAST(v_op_amount, v_inv_debt_before);
            v_prepayment_added := v_op_amount - v_payment_amount;
        END IF;

        -- Расчеты
        v_balance_after := COALESCE(v_balance_before, 0) - COALESCE(v_charge_amount, 0) + COALESCE(v_payment_amount, 0);
        v_inv_debt_after := COALESCE(v_inv_debt_before, 0) + COALESCE(v_charge_amount, 0) - COALESCE(v_payment_amount, 0);
        v_prepayment_after := COALESCE(v_prepayment_before, 0) + COALESCE(v_prepayment_added, 0) - COALESCE(v_prepayment_applied, 0);

        -- Обновление HSTORE
        IF v_inv_debt_after = 0 THEN
            -- Удаляем запись из HSTORE
            v_invoice_debt_map := v_invoice_debt_map - v_invoice_id_text;
            -- Удаляем ID из FIFO-массива
            v_fifo_invoices := array_remove(v_fifo_invoices, v_id_invoice);
        ELSIF v_inv_debt_before = 0 AND v_inv_debt_after > 0 THEN
            -- Создаем новый долг (преобразуем numeric в text)
            v_invoice_debt_map := v_invoice_debt_map || hstore(v_invoice_id_text, v_inv_debt_after::text);
            -- Добавляем в конец FIFO-массива
            v_fifo_invoices := array_append(v_fifo_invoices, v_id_invoice);
        ELSE
            -- Обновляем существующий долг
            v_invoice_debt_map := v_invoice_debt_map || hstore(v_invoice_id_text, v_inv_debt_after::text);
        END IF;

        -- Возврат строки (Основная операция)
        debt_invoices_before := NULL;
		-- debt_invoices_after := v_invoice_debt_map;
		debt_invoices_after := COALESCE(
			(SELECT jsonb_object_agg(key, value::numeric)
			FROM each(v_invoice_debt_map) AS t(key, value)),
			'{}'::jsonb
		);


        op_seq_num := v_op_seq_num;
        id_seller := rec.id_seller;
        id_payer := rec.id_payer;
        op_date := rec.op_date;
        op_date_ts := rec.op_date_ts;
        op_type := v_op_type;
        id_invoice := v_id_invoice;
        inv_amount := rec.inv_amount;
        op_amount := v_op_amount;

        balance_before := v_balance_before;
        charge_amount := v_charge_amount;
        payment_amount := v_payment_amount;
        balance_after := v_balance_after;

        prepayment_before := v_prepayment_before;
        prepayment_added := v_prepayment_added;
        prepayment_applied := v_prepayment_applied;
        prepayment_after := v_prepayment_after;

        inv_debt_before := v_inv_debt_before;
        inv_debt_after := v_inv_debt_after;

        RETURN NEXT;

        -------------------------------------------------------
        -- Зачет предоплаты (используем FIFO-массив)
        -------------------------------------------------------
        IF (v_prepayment_after > 0 AND array_length(v_fifo_invoices, 1) > 0) THEN

            FOR i IN 1..array_length(v_fifo_invoices, 1) LOOP
                EXIT WHEN v_prepayment_after <= 0;

                v_id_invoice := v_fifo_invoices[1]; -- Берем всегда первый элемент массива (!), а не i-тый
                v_invoice_id_text := v_id_invoice::text;

                -- Быстро получаем долг для зачета (O(1))
                v_inv_debt_before := COALESCE((v_invoice_debt_map -> v_invoice_id_text)::numeric, 0);

                CONTINUE WHEN v_inv_debt_before = 0; -- для надежности

                v_op_seq_num := v_op_seq_num + 1;
                v_balance_before := v_balance_after;
                v_prepayment_before := v_prepayment_after;

                v_op_type := 3; -- Зачет
                v_charge_amount := 0;
                v_prepayment_added := 0;

                v_op_amount := LEAST(v_inv_debt_before, v_prepayment_before);

                v_payment_amount := v_op_amount;
                v_prepayment_applied := v_op_amount;

                v_balance_after := v_balance_before + v_payment_amount;
                v_inv_debt_after := v_inv_debt_before - v_payment_amount;
                v_prepayment_after := v_prepayment_before - v_prepayment_applied;

                -- Обновление HSTORE после зачета
				IF v_inv_debt_after = 0 THEN
				    v_invoice_debt_map := delete(v_invoice_debt_map, v_invoice_id_text); -- удалить полностью

                    v_fifo_invoices := array_remove(v_fifo_invoices, v_id_invoice);
					-- можно и так, но быстрее не становится
				    -- v_fifo_invoices := v_fifo_invoices[2:];
				ELSE
				    v_invoice_debt_map := v_invoice_debt_map || hstore(v_invoice_id_text, v_inv_debt_after::text);
				END IF;

                -- Возврат строки (зачет)
                debt_invoices_before := NULL;
				-- debt_invoices_after := v_invoice_debt_map;
				debt_invoices_after := COALESCE(
					(SELECT jsonb_object_agg(key, value::numeric)
					FROM each(v_invoice_debt_map) AS t(key, value)),
					'{}'::jsonb
				);


                op_seq_num := v_op_seq_num;
                id_seller := rec.id_seller;
                id_payer := rec.id_payer;
                op_date := rec.op_date;
                op_date_ts := rec.op_date_ts;
                op_type := v_op_type;
                id_invoice := v_id_invoice;
                inv_amount := NULL;
                op_amount := v_op_amount;

                balance_before := v_balance_before;
                charge_amount := v_charge_amount;
                payment_amount := v_payment_amount;
                balance_after := v_balance_after;

                prepayment_before := v_prepayment_before;
                prepayment_added := v_prepayment_added;
                prepayment_applied := v_prepayment_applied;
                prepayment_after := v_prepayment_after;

                inv_debt_before := v_inv_debt_before;
                inv_debt_after := v_inv_debt_after;

                RETURN NEXT;
            END LOOP;

            -- Оставляем только счета с остаточным долгом, сохраняя исходный FIFO-порядок.
            -- Нельзя восстанавливать очередь из hstore: это хеш-структура без порядка.
            SELECT COALESCE(
                       array_agg(q.invoice_id ORDER BY q.position),
                       '{}'::integer[]
                   )
            INTO v_fifo_invoices
            FROM unnest(v_fifo_invoices) WITH ORDINALITY AS q(invoice_id, position)
            WHERE COALESCE(
                      (v_invoice_debt_map -> q.invoice_id::text)::numeric,
                      0
                  ) > 0;
        END IF;

        prev_id_seller := rec.id_seller;
        prev_id_payer := rec.id_payer;
    END LOOP;

    RETURN;
END;

$BODY$;
COMMIT;
