-- View: bergapp.balances

DROP MATERIALIZED VIEW IF EXISTS bergapp.balances;

CREATE MATERIALIZED VIEW IF NOT EXISTS bergapp.balances
AS
 WITH last_ops AS (
         SELECT DISTINCT ON (operations.id_seller, operations.id_payer) operations.op_seq_num,
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
          ORDER BY operations.id_seller, operations.id_payer, operations.op_seq_num DESC
        ), last_payments AS (
         SELECT DISTINCT ON (operations.id_seller, operations.id_payer) operations.id_seller,
            operations.id_payer,
            operations.op_date AS last_payment_date
           FROM bergapp.operations
          WHERE operations.op_type = 2
          ORDER BY operations.id_seller, operations.id_payer, operations.op_seq_num DESC
        )
 SELECT lo.id_seller,
    lo.id_payer,
    lo.prepayment_after AS prepayment,
    - lo.balance_after AS debt,
    lp.last_payment_date
   FROM last_ops lo
     LEFT JOIN last_payments lp ON lo.id_seller = lp.id_seller AND lo.id_payer = lp.id_payer
;
