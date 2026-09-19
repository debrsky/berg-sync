CREATE SCHEMA IF NOT EXISTS berg_persistent; 

DROP PROCEDURE IF EXISTS berg_persistent.archive_invoices;

CREATE OR REPLACE PROCEDURE berg_persistent.archive_invoices()
LANGUAGE plpgsql
AS $$
BEGIN

WITH current_invoices AS (
    SELECT 
        id_invoice,
        id_seller,
        id_payer,
        nomer,
        inv_date,
        row_to_json(t)::jsonb AS current_invoice_jsonb
    FROM (
        SELECT 
            i.id_invoice,
            i.nomer,
            i.inv_date,
            i.id_seller,
            i.id_payer,
            p.inn AS payer_inn,
            NULLIF(TRIM(p.kpp), '') AS payer_kpp,
            
            row_to_json(
                (SELECT seller FROM (
                    SELECT 
                        i.id_seller,
                        s.name,
                        s.inn,
                        NULLIF(TRIM(s.kpp), '') AS kpp,
                        s.ogrn,
                        s.ogrn_date,
                        s.address,
                        s.rs,
                        s.bank,
                        s.bik,
                        s.ks
                ) seller)
            ) AS seller,
            
            row_to_json(
                (SELECT payer FROM (
                    SELECT 
                        i.id_payer,
                        p.name,
                        p.inn,
                        NULLIF(TRIM(p.kpp), '') AS kpp,
                        p.address
                ) payer)
            ) AS payer,
            
            row_to_json(
                (SELECT consigner FROM (
                    SELECT 
                        i.id_consigner,
                        cer.name,
                        cer.address
                ) consigner)
            ) AS consigner,
            
            row_to_json(
                (SELECT consignee FROM (
                    SELECT 
                        i.id_consignee AS id_consignee,
                        cee.name AS name,
                        cee.address AS address
                ) consignee)
            ) AS consignee,
            
            i.app,
            i.details,
            i.total_amount_without_nds,
            i.total_nds_amount,
            i.total_amount
            
        FROM bergapp.invoices i
            LEFT JOIN bergapp.sellers s ON i.id_seller = s.id_seller
            LEFT JOIN bergapp.counterparties p ON i.id_payer = p.id_counterparty
            LEFT JOIN bergapp.counterparties cer ON i.id_consigner = cer.id_counterparty
            LEFT JOIN bergapp.counterparties cee ON i.id_consignee = cee.id_counterparty
        WHERE inv_date >= '2026-01-01'::date  -- архивируем только новые документы
    ) t
),

latest_archived AS (
    SELECT 
        id_invoice,
        invoice AS archived_invoice_jsonb,
        archived_at
    FROM berg_persistent.archived_invoices
    WHERE archived_at = (
        SELECT MAX(archived_at)
        FROM berg_persistent.archived_invoices ai
        WHERE ai.id_invoice = berg_persistent.archived_invoices.id_invoice
    )
),

comparison AS (
    SELECT 
        COALESCE(c.id_invoice, a.id_invoice) AS id_invoice,
        CASE
            WHEN c.id_invoice IS NOT NULL AND a.id_invoice IS NULL THEN 1 -- новый
            WHEN c.id_invoice IS NOT NULL AND a.id_invoice IS NOT NULL AND c.current_invoice_jsonb = a.archived_invoice_jsonb THEN 0 -- без изменений
            WHEN c.id_invoice IS NOT NULL AND a.id_invoice IS NOT NULL AND c.current_invoice_jsonb <> a.archived_invoice_jsonb THEN
                CASE
                    WHEN 
                        (c.current_invoice_jsonb ->> 'total_amount' IS DISTINCT FROM a.archived_invoice_jsonb ->> 'total_amount') OR
                        (c.current_invoice_jsonb ->> 'total_nds_amount' IS DISTINCT FROM a.archived_invoice_jsonb ->> 'total_nds_amount') OR
                        (c.current_invoice_jsonb ->> 'total_amount_without_nds' IS DISTINCT FROM a.archived_invoice_jsonb ->> 'total_amount_without_nds') OR
                        (c.current_invoice_jsonb ->> 'payer_inn' IS DISTINCT FROM a.archived_invoice_jsonb ->> 'payer_inn') OR
                        (c.current_invoice_jsonb ->> 'payer_kpp' IS DISTINCT FROM a.archived_invoice_jsonb ->> 'payer_kpp')
                    THEN 2 -- существенно изменен
                    ELSE 3 -- изменен
                END
            WHEN c.id_invoice IS NULL AND a.id_invoice IS NOT NULL THEN 9 -- удален
        END AS status,
        c.id_seller,
        c.id_payer,
        c.nomer,
        c.inv_date,
        c.current_invoice_jsonb,
        a.archived_invoice_jsonb,
        a.archived_at AS last_archived_at
    FROM current_invoices c
    FULL OUTER JOIN latest_archived a ON c.id_invoice = a.id_invoice
)

-- Вставка новых и измененных (status 1, 2, 3), удаленные игнорируем (status 9)
INSERT INTO berg_persistent.archived_invoices (
    id_invoice,
    id_seller,
    id_payer,
    nomer,
    inv_date,
    invoice,
    reason
)
SELECT
    id_invoice,
    id_seller,
    id_payer,
    nomer,
    inv_date,
    current_invoice_jsonb,
    status
FROM comparison
WHERE status IN (1, 2, 3);


END;
$$;