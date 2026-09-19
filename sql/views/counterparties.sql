DROP MATERIALIZED VIEW IF EXISTS bergapp.counterparties;

CREATE MATERIALIZED VIEW bergapp.counterparties
AS
WITH relevant_apps AS (
    SELECT DISTINCT
        app."ID_CustomerPay" AS id_counterparty
    FROM bergauto."XInvoices" inv
    JOIN bergauto."Applications" app ON app."ID" = inv."ID_Application"
    WHERE inv."Nomer" <> 0

    UNION

    SELECT DISTINCT
        app."ID_Customer" AS id_counterparty
    FROM bergauto."XInvoices" inv
    JOIN bergauto."Applications" app ON app."ID" = inv."ID_Application"
    WHERE inv."Nomer" <> 0

    UNION

    SELECT DISTINCT
        app."ID_CustomerOut" AS id_counterparty
    FROM bergauto."XInvoices" inv
    JOIN bergauto."Applications" app ON app."ID" = inv."ID_Application"
    WHERE inv."Nomer" <> 0
)
SELECT 
    c."ID" AS id_counterparty,
    c."NameShort" AS name,
    c."Tel" AS tel,
    c."Address" AS address,
    c."INN" AS inn,
    c."KPP" AS kpp
FROM bergauto."Customers" c
WHERE c."ID" IN (SELECT id_counterparty FROM relevant_apps)
WITH DATA;

CREATE UNIQUE INDEX IF NOT EXISTS idx_counterparties_id_counterparty 
ON bergapp.counterparties (id_counterparty);