DROP MATERIALIZED VIEW IF EXISTS bergapp.debt_invoices;

CREATE MATERIALIZED VIEW IF NOT EXISTS bergapp.debt_invoices
AS
 SELECT o.id_seller,
    o.id_payer,
    debt_item.key::integer AS id_invoice,
    debt_item.value::numeric AS debt_amount
   FROM ( SELECT DISTINCT ON (operations.id_seller, operations.id_payer) operations.op_seq_num,
            operations.id_seller,
            operations.id_payer,
            operations.op_date,
            operations.op_date_ts,
            operations.op_type,
            operations.id_invoice,
            operations.inv_amount,
            operations.op_amount,
            operations.balance_before,
            operations.charge_amount,
            operations.payment_amount,
            operations.balance_after,
            operations.prepayment_before,
            operations.prepayment_added,
            operations.prepayment_applied,
            operations.prepayment_after,
            operations.inv_debt_before,
            operations.inv_debt_after,
            operations.debt_invoices_before,
            operations.debt_invoices_after
           FROM bergapp.operations
          ORDER BY operations.id_seller, operations.id_payer, operations.op_seq_num DESC) o
     CROSS JOIN LATERAL jsonb_each(o.debt_invoices_after) debt_item(key, value)
  WHERE jsonb_typeof(o.debt_invoices_after) = 'object'::text AND o.debt_invoices_after <> '{}'::jsonb
;
