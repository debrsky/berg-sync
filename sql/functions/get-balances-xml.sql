DROP FUNCTION IF EXISTS bergapp.get_balances_xml(date);

CREATE OR REPLACE FUNCTION bergapp.get_balances_xml(
	p_cutoff_date date DEFAULT NULL::date)
    RETURNS text
    LANGUAGE 'plpgsql'
    COST 100
    IMMUTABLE PARALLEL UNSAFE
AS $BODY$
DECLARE
    xml_content TEXT;
    v_cutoff_text TEXT;
    v_export_timestamp TEXT;
BEGIN
    -- Человекочитаемое описание обрезки
    IF p_cutoff_date IS NULL THEN
        v_cutoff_text := 'без ограничения (самый актуальный срез)';
    ELSE
        v_cutoff_text := to_char(p_cutoff_date, 'YYYY-MM-DD');
    END IF;

    -- Время в ЧАСОВОМ ПОЯСЕ КЛИЕНТА с offset (например, 2025-12-22 17:45:30+05:00)
    v_export_timestamp := to_char(current_timestamp, 'YYYY-MM-DD HH24:MI:SSOF');

    SELECT xmlserialize(CONTENT
        xmlelement(name "balances",
            xmlattributes(
                v_export_timestamp AS "export_timestamp",
                p_cutoff_date AS "cutoff_date"
            ),
            xmlelement(name "cutoff_description", v_cutoff_text),
            xmlagg(
                xmlelement(name "balance",
                    xmlelement(name "id_seller", id_seller),
                    xmlelement(name "id_payer", id_payer),
                    xmlelement(name "last_date", op_date),
                    xmlelement(name "debt", debt),
                    xmlelement(name "prepayment", prepayment)
                )
            )
        ) AS TEXT
    )
    INTO xml_content
    FROM (
        SELECT 
            id_seller,
            id_payer,
            op_date,
            -balance_after AS debt,
            prepayment_after AS prepayment
        FROM (
            SELECT 
                id_seller,
                id_payer,
                op_date,
                balance_after,
                prepayment_after,
                ROW_NUMBER() OVER (
                    PARTITION BY id_seller, id_payer 
                    ORDER BY op_seq_num DESC
                ) AS rn
            FROM bergapp.operations op
            WHERE p_cutoff_date IS NULL
               OR op.op_date < p_cutoff_date
        ) t
        WHERE rn = 1
    ) final;

    RETURN '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' || xml_content;
END;
$BODY$;

