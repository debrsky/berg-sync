-- View: bergapp.invoices

DROP MATERIALIZED VIEW IF EXISTS bergapp.invoices;

CREATE MATERIALIZED VIEW IF NOT EXISTS bergapp.invoices
AS

WITH inv_base AS (
    SELECT 
        inv."ID",
        inv."ID_Boss",
        app."ID_CustomerPay",
        app."ID_Customer",
        app."ID_CustomerOut",
        inv."Nomer",
        inv."Date"::date AS inv_date,
        inv."Date" AS inv_date_ts,
        -- app."StartInPlan"::date AS shipment_date,
        -- app."EndOutPlan"::date AS delivery_date,
        round(inv."Cost"::numeric, 2) AS amount,  -- Оставляем для совместимости, но итоги считаем отдельно
        CASE
            WHEN inv."Date"::date < '2026-01-01'::date THEN 0::numeric
            ELSE b."NDS"::numeric
        END AS nds,
        inv."Name" AS content,
        inv."Mem" AS memo,
        app."ID" AS app_id,
        app."Nomer" AS app_nomer,
        convert_from(set_byte('\x00'::bytea, 0, app."BaseCode"::integer), 'WIN1251'::name) AS app_base_code,
        app."DateReg"::date AS app_date_reg,
        c."Name" AS app_cargo,
        round((app."Weight" * 1000::double precision)::numeric, 0) AS app_weight,
        round(app."Volume"::numeric, 2) AS app_volume,
        app."CountPcs"::numeric AS app_count_pcs,
        inv."IsFixed",
        inv."ID_Application",
        app."IsCash"
    FROM bergauto."XInvoices" inv
    JOIN bergauto."Applications" app ON app."ID" = inv."ID_Application"
    JOIN bergauto."Bosses" b ON b."ID" = inv."ID_Boss"
    JOIN bergauto."Cargos" c ON c."ID" = app."ID_Cargo"
    WHERE 
        inv."Nomer" <> 0
        -- AND inv."Date"::date >= '2025-12-01'::date
)
SELECT 
    "ID" AS id_invoice,
    "ID_Boss" AS id_seller,
    "ID_CustomerPay" AS id_payer,
    "ID_Customer" AS id_consigner,
    "ID_CustomerOut" AS id_consignee,
    "Nomer" AS nomer,
    inv_date,
    inv_date_ts,
    -- shipment_date,
    -- delivery_date,
    amount,  -- Общий amount из инвойса (для справки)
    nds,
    -- content,
    memo,
    jsonb_build_object(
        'id_app', app_id,
        'nomer', app_nomer,
        'base_code', app_base_code,
        'date_reg', app_date_reg,
        'cargo', app_cargo,
        'weight', app_weight,
        'volume', app_volume,
        'count_pcs', app_count_pcs
    ) AS app,
    CASE
        WHEN "IsFixed" THEN 
            jsonb_build_array(
                jsonb_build_object(
                    'pos', 0,
                    'name', content,
                    'price', amount,
                    'qty', 1,
                    'mUcode', '796',
                    'mU', 'шт',
                    'amount', amount,
                    'nds', nds,
                    'nds_amount', round(amount * (nds / (100 + nds)), 2),
                    'amount_without_nds', amount - round(amount * (nds / (100 + nds)), 2),
                    'price_without_nds', amount - round(amount * (nds / (100 + nds)), 2)  -- Поскольку qty=1
                )
            )
        ELSE 
            COALESCE(
                (
                    SELECT 
                        jsonb_agg(
                            jsonb_build_object(
                                'pos', invd."Pos",
                                'name', invd."Name",
                                'price', round(invd."Price"::numeric, 2),
                                'qty', invd."Amount",
                                'mUcode', CASE invd."TypeEd"
                                    WHEN 0 THEN '796'
                                    WHEN 1 THEN '166'
                                    WHEN 2 THEN '113'
                                    WHEN 3 THEN '356'
                                    ELSE '--'
                                END,
                                'mU', CASE invd."TypeEd"
                                    WHEN 0 THEN 'шт'
                                    WHEN 1 THEN 'кг'
                                    WHEN 2 THEN 'м³'
                                    WHEN 3 THEN 'час'
                                    ELSE 'рейс'
                                END,
                                'amount', round((invd."Price" * invd."Amount")::numeric, 2),
                                'nds', nds,
                                'nds_amount', round(round((invd."Price" * invd."Amount")::numeric, 2) * (nds / (100 + nds)), 2),
                                'amount_without_nds', round((invd."Price" * invd."Amount")::numeric, 2) 
                                    - round(round((invd."Price" * invd."Amount")::numeric, 2) * (nds / (100 + nds)), 2),
                                'price_without_nds', round(
                                    CASE 
                                        WHEN invd."Amount" = 0 THEN 0::numeric
                                        ELSE (
                                            round(
                                                round((invd."Price" * invd."Amount")::numeric, 2) 
                                                - round(round((invd."Price" * invd."Amount")::numeric, 2) * (nds / (100 + nds)), 2),
                                                2
                                            ) / invd."Amount"
                                        )::numeric
                                    END, 6
                                )
                            ) ORDER BY invd."Pos"
                        ) AS jsonb_agg
                    FROM bergauto."XInvoiceDatas" invd
                    WHERE invd."ID_XInvoice" = inv_base."ID" AND invd."IsFree" = false
                ), 
                jsonb_build_array(
                    jsonb_build_object(
                        'pos', 0,
                        'name', content,
                        'price', amount,
                        'qty', 1,
                        'mUcode', '796',
                        'mU', 'шт',
                        'amount', amount,
                        'nds', nds,
                        'nds_amount', round(amount * (nds / (100 + nds)), 2),
                        'amount_without_nds', amount - round(amount * (nds / (100 + nds)), 2),
                        'price_without_nds', amount - round(amount * (nds / (100 + nds)), 2)
                    )
                )
            )
    END AS details,
    -- Новые итоги: сумма по позициям (явно, без опоры на общий amount)
    CASE
        WHEN "IsFixed" THEN amount  -- Сумма amount по единственной позиции
        ELSE COALESCE(
            (SELECT round(SUM(round((invd."Price" * invd."Amount")::numeric, 2))::numeric, 2)
             FROM bergauto."XInvoiceDatas" invd
             WHERE invd."ID_XInvoice" = inv_base."ID" AND invd."IsFree" = false),
            amount  -- Дефолт, если нет позиций
        )
    END AS total_amount,
    CASE
        WHEN "IsFixed" THEN round(amount * (nds / (100 + nds)), 2)  -- Сумма nds_amount по позиции
        ELSE COALESCE(
            (SELECT round(SUM(round(round((invd."Price" * invd."Amount")::numeric, 2) * (nds / (100 + nds)), 2))::numeric, 2)
             FROM bergauto."XInvoiceDatas" invd
             WHERE invd."ID_XInvoice" = inv_base."ID" AND invd."IsFree" = false),
            round(amount * (nds / (100 + nds)), 2)  -- Дефолт
        )
    END AS total_nds_amount,
    CASE
        WHEN "IsFixed" THEN amount - round(amount * (nds / (100 + nds)), 2)  -- Сумма amount_without_nds по позиции
        ELSE COALESCE(
            (SELECT round(SUM(
                round((invd."Price" * invd."Amount")::numeric, 2) 
                - round(round((invd."Price" * invd."Amount")::numeric, 2) * (nds / (100 + nds)), 2)
            )::numeric, 2)
             FROM bergauto."XInvoiceDatas" invd
             WHERE invd."ID_XInvoice" = inv_base."ID" AND invd."IsFree" = false),
            amount - round(amount * (nds / (100 + nds)), 2)  -- Дефолт
        )
    END AS total_amount_without_nds,
    "IsCash" AS is_cash
FROM inv_base

WITH DATA;

CREATE UNIQUE INDEX IF NOT EXISTS idx_invoices_id_invoice 
ON bergapp.invoices (id_invoice);

-- Индексы на поля JOIN'ов (foreign keys в invoices)
CREATE INDEX IF NOT EXISTS idx_invoices_id_seller 
ON bergapp.invoices (id_seller);

CREATE INDEX IF NOT EXISTS idx_invoices_id_payer 
ON bergapp.invoices (id_payer);

CREATE INDEX IF NOT EXISTS idx_invoices_id_consigner 
ON bergapp.invoices (id_consigner);

CREATE INDEX IF NOT EXISTS idx_invoices_id_consignee 
ON bergapp.invoices (id_consignee);