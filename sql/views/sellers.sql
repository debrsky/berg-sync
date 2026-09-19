-- View: bergapp.sellers

DROP MATERIALIZED VIEW IF EXISTS bergapp.sellers;

CREATE MATERIALIZED VIEW IF NOT EXISTS bergapp.sellers
TABLESPACE pg_default
AS
 SELECT "ID" AS id_seller,
    "Name" AS name,
    "BossName" AS ceo,
    "Address" AS address,
    "INN" AS inn,
        CASE
            WHEN length("INN"::text) = 10 THEN "KPP"
            ELSE NULL::character varying
        END AS kpp,
    "OGRN" AS ogrn,
    CASE
        WHEN "OGRN" = '304253930300041' THEN '2004-10-29'::date -- Горшунов
        WHEN "OGRN" = '304253726100112' THEN '2004-09-17'::date -- Берг
        WHEN "OGRN" = '304253820500080' THEN '2004-07-23'::date -- Балобаев
        WHEN "OGRN" = '325253600048174' THEN '2027-05-25'::date -- Коновалов
        ELSE NULL
    END AS ogrn_date,
    "RS" AS rs,
    "Bank" AS bank,
    "BIK" AS bik,
    "KS" AS ks,
    "NDS"::numeric AS nds
   FROM bergauto."Bosses"
  WHERE ("ID" IN ( SELECT DISTINCT operations.id_seller
           FROM bergapp.operations))
;

CREATE UNIQUE INDEX IF NOT EXISTS idx_sellers_id_seller 
ON bergapp.sellers (id_seller);
