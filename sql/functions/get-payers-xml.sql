DROP FUNCTION IF EXISTS bergapp.get_payers_xml();

CREATE OR REPLACE FUNCTION bergapp.get_payers_xml(
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
        xmlelement(name "payers",
            xmlattributes(
                v_export_timestamp AS "export_timestamp"
            ),
            xmlagg(
                xmlelement(name "payer",
                    xmlelement(name "id_payer",   id_payer),
                    xmlelement(name "name",       name),
                    xmlelement(name "tel",        tel),
                    xmlelement(name "address",    address),
                    xmlelement(name "inn",        inn),
                    xmlelement(name "kpp",        kpp)
                )
                ORDER BY id_payer  -- <-- вот здесь порядок!
            )
        ) AS TEXT
    )
    INTO xml_content
    FROM bergapp.payers;

    RETURN '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' || xml_content;
END;
$BODY$;

