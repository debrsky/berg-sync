BEGIN;
DROP TABLE IF EXISTS bergapp.operations;
COMMIT;

BEGIN;
CREATE TABLE bergapp.operations (
    op_seq_num integer NOT NULL,
    id_seller integer NOT NULL,
    id_payer integer NOT NULL,
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
    debt_invoices_after jsonb,

    CONSTRAINT operations_pkey PRIMARY KEY (id_seller, id_payer, op_seq_num)
);

-- Первичный ключ одновременно ускоряет замену операций отдельных финансовых пар.

-- Дополнительные индексы пока не создаём без подтверждённой нагрузки чтения.
-- CREATE INDEX operations_seller_payer_seq_idx ON bergapp.operations (id_seller, id_payer, op_seq_num DESC);
-- CREATE INDEX IF NOT EXISTS idx_operations_date ON bergapp.operations(op_date);
COMMIT;


BEGIN;
CREATE OR REPLACE PROCEDURE bergapp.calculate_and_save_operations(
    p_id_payer integer DEFAULT NULL::integer,
    p_id_seller integer DEFAULT NULL::integer
)
LANGUAGE plpgsql
AS $BODY$
BEGIN
    -- 1. Очистка таблицы
    -- Если параметры не переданы, делаем полный сброс (TRUNCATE работает быстрее DELETE)
    IF p_id_payer IS NULL AND p_id_seller IS NULL THEN
        RAISE NOTICE 'Выполняется полная очистка таблицы operations (TRUNCATE)...';
        TRUNCATE TABLE bergapp.operations;
    ELSE
        -- Если передан фильтр, удаляем только старые записи по этим контрагентам
        RAISE NOTICE 'Удаление старых записей для Payer: %, Seller: % ...', p_id_payer, p_id_seller;
        DELETE FROM bergapp.operations
        WHERE (p_id_payer IS NULL OR id_payer = p_id_payer)
          AND (p_id_seller IS NULL OR id_seller = p_id_seller);
    END IF;

    -- 2. Расчет и вставка новых данных
    -- Используем INSERT INTO ... SELECT, так как это наиболее производительный способ
    -- (bulk insert), сохраняющий скорость работы вашей оптимизированной функции.
    RAISE NOTICE 'Начало расчета и вставки данных...';
    
    INSERT INTO bergapp.operations (
        op_seq_num, id_seller, id_payer, op_date, op_date_ts, 
        op_type, id_invoice, inv_amount, op_amount, 
        balance_before, charge_amount, payment_amount, balance_after, 
        prepayment_before, prepayment_added, prepayment_applied, prepayment_after, 
        inv_debt_before, inv_debt_after, 
        debt_invoices_before, debt_invoices_after
    )
    SELECT 
        op_seq_num, id_seller, id_payer, op_date, op_date_ts, 
        op_type, id_invoice, inv_amount, op_amount, 
        balance_before, charge_amount, payment_amount, balance_after, 
        prepayment_before, prepayment_added, prepayment_applied, prepayment_after, 
        inv_debt_before, inv_debt_after, 
        debt_invoices_before, debt_invoices_after
    FROM bergapp.get_operations(p_id_payer, p_id_seller);

    RAISE NOTICE 'Операция завершена успешно.';
END;
$BODY$;

COMMIT;
