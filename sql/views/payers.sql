DROP MATERIALIZED VIEW IF EXISTS bergapp.payers;

CREATE MATERIALIZED VIEW bergapp.payers
TABLESPACE pg_default
AS
 SELECT 
 	"ID" AS id_payer,
    "NameShort" AS name,
    "Tel" AS tel,
    "Address" AS address,
    "INN" AS inn,
    -- "OGRN",
    "KPP" AS kpp
    -- "RS",
    -- "KS",
    -- "BIK",
    -- "Bank",
    -- "BaseCode",
    -- "ID_Ref",
    -- "ToExport"
   FROM bergauto."Customers"
  WHERE ("ID" IN ( SELECT DISTINCT operations.id_payer
           FROM bergapp.operations))
;

