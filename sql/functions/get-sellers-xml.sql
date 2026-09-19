DROP FUNCTION IF EXISTS bergapp.get_sellers_xml();

CREATE OR REPLACE FUNCTION bergapp.get_sellers_xml(
	)
    RETURNS text
    LANGUAGE 'plpgsql'
    COST 100
    IMMUTABLE PARALLEL UNSAFE
AS $BODY$
DECLARE
    xml_content        TEXT;
    v_export_timestamp TEXT;
BEGIN
    -- Timestamp с offset в часовом поясе сервера
    v_export_timestamp := to_char(current_timestamp, 'YYYY-MM-DD HH24:MI:SSOF');

    SELECT xmlserialize(CONTENT
        xmlelement(name "sellers",
            xmlattributes(
                v_export_timestamp AS "export_timestamp"
            ),
            xmlagg(
                xmlelement(name "seller",
                    xmlelement(name "id_seller",   id_seller),
                    xmlelement(name "name",       name),
                    xmlelement(name "ceo",        ceo),
                    xmlelement(name "vat",        nds),
                    xmlelement(name "address",    address),
                    xmlelement(name "inn",        inn),
                    xmlelement(name "kpp",        kpp),
                    xmlelement(name "ogrn",       ogrn),
                    xmlelement(name "rs",         rs),
                    xmlelement(name "bank",       bank),
                    xmlelement(name "bik",        bik),
                    xmlelement(name "ks",         ks)
                )
                ORDER BY id_seller
            )
        ) AS TEXT
    )
    INTO xml_content
    FROM bergapp.sellers;

    RETURN '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' || xml_content;
END;
$BODY$;
