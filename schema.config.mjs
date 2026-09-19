// schema.config.js
// Полная схема базы данных bergauto для PostgreSQL
// Включает структуру таблиц + все необходимые индексы

export const SchemaConfig = {
  Bosses: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "Name", Type: "VARCHAR(100)", NotNull: true },
    { Name: "BossName", Type: "VARCHAR(50)" },
    { Name: "Mem", Type: "TEXT" },
    { Name: "Tel", Type: "VARCHAR(50)" },
    { Name: "Address", Type: "VARCHAR(100)" },
    { Name: "INN", Type: "VARCHAR(50)" },
    { Name: "OGRN", Type: "VARCHAR(50)" },
    { Name: "KPP", Type: "VARCHAR(50)" },
    { Name: "RS", Type: "VARCHAR(50)" },
    { Name: "KS", Type: "VARCHAR(50)" },
    { Name: "BIK", Type: "VARCHAR(50)" },
    { Name: "Bank", Type: "VARCHAR(100)" },
    { Name: "RS2", Type: "VARCHAR(50)" },
    { Name: "KS2", Type: "VARCHAR(50)" },
    { Name: "BIK2", Type: "VARCHAR(50)" },
    { Name: "Bank2", Type: "VARCHAR(100)" },
    { Name: "BuhName", Type: "VARCHAR(50)" },
    { Name: "NextInvoiceNomer", Type: "INTEGER" },
    { Name: "NDS", Type: "DOUBLE PRECISION" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", Default: "false" },
  ],

  Cars: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "ID_Boss", Type: "INTEGER", NotNull: true }, // Перевозчик
    { Name: "Name", Type: "VARCHAR(50)", NotNull: true },                                   // Марка в документах
    { Name: "Nick", Type: "VARCHAR(50)", NotNull: true, Unique: true },                    // Позывной (уникальный!)
    { Name: "IsRented", Type: "BOOLEAN", Default: "false" },
    { Name: "RegNoGos", Type: "VARCHAR(50)", NotNull: true },                                   // Гос номер
    { Name: "RegNoGos1", Type: "VARCHAR(50)" },                                                    // Полуприцеп
    { Name: "Carring1", Type: "DOUBLE PRECISION", NotNull: true },                                // Грузоподъёмность основного ТС
    { Name: "Volume1", Type: "DOUBLE PRECISION", NotNull: true },                                // Объём основного ТС
    { Name: "Length1", Type: "DOUBLE PRECISION" },
    { Name: "Width1", Type: "DOUBLE PRECISION" },
    { Name: "Height1", Type: "DOUBLE PRECISION" },
    { Name: "RegNoGos2", Type: "VARCHAR(50)" },                                                    // Прицеп
    { Name: "Carring2", Type: "DOUBLE PRECISION" },
    { Name: "Volume2", Type: "DOUBLE PRECISION" },
    { Name: "Length2", Type: "DOUBLE PRECISION" },
    { Name: "Width2", Type: "DOUBLE PRECISION" },
    { Name: "Height2", Type: "DOUBLE PRECISION" },
    { Name: "ID_Driver1", Type: "INTEGER", NotNull: true },                                   // Основной водитель
    { Name: "ID_Driver2", Type: "INTEGER" },                                                        // Второй водитель (может быть NULL)
    { Name: "IsRef", Type: "BOOLEAN", Default: "false" },                                // Рефрижератор?
    { Name: "IsDiffCargo", Type: "BOOLEAN", Default: "false" },                                // Готовность под несовместимые грузы
    { Name: "Mem", Type: "TEXT" },
    { Name: "ID_CarType", Type: "INTEGER", NotNull: true },                                   // Тип машины по возможностям загрузки
    { Name: "ID_PointType", Type: "INTEGER", NotNull: true },                                   // Категория пунктов
    { Name: "IsUseSecondAccount", Type: "BOOLEAN", Default: "false" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", Default: "false" },
  ],

  Cargos: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "Name", Type: "VARCHAR(100)", NotNull: true },
    { Name: "IsFood", Type: "BOOLEAN", Default: "false" },
    { Name: "IsRef", Type: "BOOLEAN", Default: "false" },
    { Name: "IsFull", Type: "BOOLEAN", Default: "false" },
    { Name: "Mem", Type: "TEXT" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", Default: "false" },
  ],

  Customers: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "NameShort", Type: "VARCHAR(100)", NotNull: true },
    { Name: "ID_Cargo", Type: "INTEGER" },
    { Name: "ManIn", Type: "VARCHAR(100)" },
    { Name: "ManOut", Type: "VARCHAR(100)" },
    { Name: "IsCash", Type: "BOOLEAN", Default: "false" },
    { Name: "Mem", Type: "TEXT" },
    { Name: "ID_Tariff", Type: "INTEGER", NotNull: true },
    { Name: "Tel", Type: "VARCHAR(50)" },
    { Name: "Address", Type: "VARCHAR(100)" },
    { Name: "INN", Type: "VARCHAR(50)" },
    { Name: "OGRN", Type: "VARCHAR(50)" },
    { Name: "KPP", Type: "VARCHAR(50)" },
    { Name: "RS", Type: "VARCHAR(50)" },
    { Name: "KS", Type: "VARCHAR(50)" },
    { Name: "BIK", Type: "VARCHAR(50)" },
    { Name: "Bank", Type: "VARCHAR(100)" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", Default: "false" },
  ],

  Tariffs: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "Name", Type: "VARCHAR(50)", NotNull: true },
    { Name: "Wei0", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "Wei1", Type: "DOUBLE PRECISION" },
    { Name: "Wei2", Type: "DOUBLE PRECISION" },
    { Name: "Wei3", Type: "DOUBLE PRECISION" },
    { Name: "Wei4", Type: "DOUBLE PRECISION" },
    { Name: "Wei5", Type: "DOUBLE PRECISION" },
    { Name: "WeiPrice0", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "WeiPrice1", Type: "DOUBLE PRECISION" },
    { Name: "WeiPrice2", Type: "DOUBLE PRECISION" },
    { Name: "WeiPrice3", Type: "DOUBLE PRECISION" },
    { Name: "WeiPrice4", Type: "DOUBLE PRECISION" },
    { Name: "WeiPrice5", Type: "DOUBLE PRECISION" },
    { Name: "Vol0", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "Vol1", Type: "DOUBLE PRECISION" },
    { Name: "Vol2", Type: "DOUBLE PRECISION" },
    { Name: "Vol3", Type: "DOUBLE PRECISION" },
    { Name: "Vol4", Type: "DOUBLE PRECISION" },
    { Name: "Vol5", Type: "DOUBLE PRECISION" },
    { Name: "VolPrice0", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "VolPrice1", Type: "DOUBLE PRECISION" },
    { Name: "VolPrice2", Type: "DOUBLE PRECISION" },
    { Name: "VolPrice3", Type: "DOUBLE PRECISION" },
    { Name: "VolPrice4", Type: "DOUBLE PRECISION" },
    { Name: "VolPrice5", Type: "DOUBLE PRECISION" },
    { Name: "MinPrice", Type: "DOUBLE PRECISION" },
    { Name: "MinPrice2", Type: "DOUBLE PRECISION" },
    { Name: "FreeInOutWei", Type: "DOUBLE PRECISION" },
    { Name: "FreeInOutVol", Type: "DOUBLE PRECISION" },
    { Name: "PriceHourKat1", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "PriceHourKat2", Type: "DOUBLE PRECISION" },
    { Name: "PriceHourKat3", Type: "DOUBLE PRECISION" },
    { Name: "PriceHourKat4", Type: "DOUBLE PRECISION" },
    { Name: "PriceHourKat5", Type: "DOUBLE PRECISION" },
    { Name: "PriceHourSleep", Type: "DOUBLE PRECISION" },
    { Name: "PriceRefReic", Type: "DOUBLE PRECISION" },
    { Name: "Mem", Type: "TEXT" },
    { Name: "AddHour", Type: "DOUBLE PRECISION" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", Default: "false" },
  ],

  xSaldo: [
    { Name: "ID_CustomerPay", Type: "INTEGER", NotNull: true },
    { Name: "ID_Boss", Type: "INTEGER", NotNull: true },
    { Name: "IsCash", Type: "BOOLEAN", NotNull: true, Default: "false" },
    { Name: "Count", Type: "INTEGER", NotNull: true },
    { Name: "Cost", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "Pays", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "IsNomer", Type: "BOOLEAN", NotNull: true, Default: "false" },
    { Name: "BuildID", Type: "INTEGER" },
  ],

  Applications: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "DateReg", Type: "TIMESTAMP", NotNull: true, Default: "NOW()" },
    { Name: "TimeInStart", Type: "INTEGER" },
    { Name: "IsFixedIn", Type: "BOOLEAN", Default: "false" },
    { Name: "TimeOutStart", Type: "INTEGER" },
    { Name: "IsFixedOut", Type: "BOOLEAN", Default: "false" },
    { Name: "ID_Customer", Type: "INTEGER", NotNull: true },
    { Name: "ID_CustomerOut", Type: "INTEGER", NotNull: true },
    { Name: "ID_PointIn", Type: "INTEGER", NotNull: true },
    { Name: "ID_PointOut", Type: "INTEGER", NotNull: true },
    { Name: "IsSelfIn", Type: "BOOLEAN", Default: "false" },
    { Name: "IsSelfOut", Type: "BOOLEAN", Default: "false" },
    { Name: "ID_Cargo", Type: "INTEGER", NotNull: true },
    { Name: "Weight", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "Volume", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "CountPcs", Type: "DOUBLE PRECISION" },
    { Name: "IsCash", Type: "BOOLEAN", Default: "false" },
    { Name: "Mem", Type: "TEXT" },
    { Name: "Sum", Type: "DOUBLE PRECISION" },
    { Name: "TimeIn", Type: "INTEGER" },
    { Name: "TimeOut", Type: "INTEGER" },
    { Name: "ManIn", Type: "VARCHAR(100)" },
    { Name: "ManOut", Type: "VARCHAR(100)" },
    { Name: "StartInPlan", Type: "TIMESTAMP" },
    { Name: "EndInPlan", Type: "TIMESTAMP" },
    { Name: "StartOutPlan", Type: "TIMESTAMP" },
    { Name: "EndOutPlan", Type: "TIMESTAMP" },
    { Name: "ID_Task", Type: "INTEGER" },
    { Name: "IsInsure", Type: "BOOLEAN", Default: "false" },
    { Name: "Total", Type: "DOUBLE PRECISION" },
    { Name: "IsGuard", Type: "BOOLEAN", Default: "false" },
    { Name: "DateWorkIn", Type: "TIMESTAMP", NotNull: true, Default: "(NOW() + INTERVAL '1 day')" },
    { Name: "DateWorkOut", Type: "TIMESTAMP" },
    { Name: "ID_Tariff", Type: "INTEGER", NotNull: true },
    { Name: "SumInsure", Type: "DOUBLE PRECISION" },
    { Name: "SumGuard", Type: "DOUBLE PRECISION" },
    { Name: "Stage", Type: "INTEGER" },
    { Name: "ID_CustomerPay", Type: "INTEGER", NotNull: true },
    { Name: "IsCashIN", Type: "BOOLEAN", Default: "false" },
    { Name: "IsCashOUT", Type: "BOOLEAN", Default: "false" },
    { Name: "ID_XInvoice", Type: "INTEGER" },
    { Name: "SumUser", Type: "DOUBLE PRECISION" },
    { Name: "SumUserText", Type: "TEXT" },
    { Name: "IsWantInvoice", Type: "BOOLEAN", Default: "false" },
    { Name: "Nomer", Type: "INTEGER" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", Default: "false" },
  ],

  XInvoices: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "ID_Application", Type: "INTEGER", NotNull: true },
    { Name: "Nomer", Type: "INTEGER" },
    { Name: "Date", Type: "TIMESTAMP", NotNull: true },
    { Name: "Cost", Type: "DOUBLE PRECISION" },
    { Name: "IsFixed", Type: "BOOLEAN", NotNull: true, Default: "false" },
    { Name: "Pays", Type: "DOUBLE PRECISION" },
    { Name: "Name", Type: "TEXT", NotNull: true },
    { Name: "Mem", Type: "TEXT" },
    { Name: "ID_Boss", Type: "INTEGER", NotNull: true },
    { Name: "IsUseSecondAccount", Type: "BOOLEAN", NotNull: true, Default: "false" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", NotNull: true, Default: "false" },
  ],

  XInvoicePays: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "ID_XInvoice", Type: "INTEGER", NotNull: true },
    { Name: "Date", Type: "TIMESTAMP", NotNull: true },
    { Name: "Cost", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "Mem", Type: "TEXT" },
    { Name: "Name", Type: "VARCHAR(50)" },
    { Name: "IsCash", Type: "BOOLEAN", NotNull: true, Default: "false" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", NotNull: true, Default: "false" },
  ],

  XInvoiceDatas: [
    { Name: "ID", Type: "INTEGER", PK: true },
    { Name: "ID_XInvoice", Type: "INTEGER", NotNull: true },
    { Name: "Pos", Type: "INTEGER", NotNull: true },
    { Name: "Name", Type: "TEXT", NotNull: true },
    { Name: "TypeEd", Type: "INTEGER", NotNull: true },
    { Name: "Amount", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "Price", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "Cost", Type: "DOUBLE PRECISION", NotNull: true },
    { Name: "ID_Car", Type: "INTEGER" },
    { Name: "Mem", Type: "TEXT" },
    { Name: "Type", Type: "INTEGER", NotNull: true },
    { Name: "IsFree", Type: "BOOLEAN", NotNull: true, Default: "false" },
    { Name: "Len", Type: "DOUBLE PRECISION" },
    { Name: "Hours", Type: "DOUBLE PRECISION" },
    { Name: "BaseCode", Type: "SMALLINT" },
    { Name: "ID_Ref", Type: "INTEGER" },
    { Name: "ToExport", Type: "BOOLEAN", NotNull: true, Default: "false" },
  ],
};

// =========================================
// Все индексы — теперь в одном месте!
// =========================================

export const IndexesConfig = {
  Applications: [
    `CREATE INDEX IF NOT EXISTS idx_applications_date_reg          ON "Applications" ("DateReg")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_datework_in       ON "Applications" ("DateWorkIn")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_datework_out      ON "Applications" ("DateWorkOut")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_customer          ON "Applications" ("ID_Customer")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_customer_pay      ON "Applications" ("ID_CustomerPay")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_customer_out      ON "Applications" ("ID_CustomerOut")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_point_in          ON "Applications" ("ID_PointIn")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_point_out         ON "Applications" ("ID_PointOut")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_cargo             ON "Applications" ("ID_Cargo")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_tariff            ON "Applications" ("ID_Tariff")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_stage             ON "Applications" ("Stage")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_nomer             ON "Applications" ("Nomer")`,
    `CREATE INDEX IF NOT EXISTS idx_applications_customer_date     ON "Applications" ("ID_Customer", "DateReg" DESC)`,
    `CREATE INDEX IF NOT EXISTS idx_applications_pay_date          ON "Applications" ("ID_CustomerPay", "DateReg" DESC)`,
    `CREATE INDEX IF NOT EXISTS idx_applications_period            ON "Applications" ("DateWorkIn", "DateWorkOut")`,
  ],

  XInvoices: [
    `CREATE INDEX IF NOT EXISTS idx_xinvoices_app            ON "XInvoices" ("ID_Application")`,
    `CREATE INDEX IF NOT EXISTS idx_xinvoices_date           ON "XInvoices" ("Date" DESC)`,
    `CREATE INDEX IF NOT EXISTS idx_xinvoices_nomer          ON "XInvoices" ("Nomer")`,
    `CREATE INDEX IF NOT EXISTS idx_xinvoices_boss           ON "XInvoices" ("ID_Boss")`,
    `CREATE INDEX IF NOT EXISTS idx_xinvoices_fixed          ON "XInvoices" ("IsFixed")`,
    `CREATE INDEX IF NOT EXISTS ix_xinvoices_covering        ON "XInvoices" ("Date" ASC, "Nomer") INCLUDE ("ID", "ID_Boss", "Cost", "Name", "IsFixed", "Mem", "ID_Application") WHERE "Nomer" <> 0;`
  ],

  XInvoicePays: [
    `CREATE INDEX IF NOT EXISTS idx_xinvoicepays_invoice ON "XInvoicePays" ("ID_XInvoice")`,
    `CREATE INDEX IF NOT EXISTS idx_xinvoicepays_date     ON "XInvoicePays" ("Date" DESC)`,
  ],

  XInvoiceDatas: [
    `CREATE INDEX IF NOT EXISTS idx_xinvoicedatas_invoice ON "XInvoiceDatas" ("ID_XInvoice")`,
    `CREATE INDEX IF NOT EXISTS idx_xinvoicedatas_car     ON "XInvoiceDatas" ("ID_Car") WHERE "ID_Car" IS NOT NULL`,
  ],

  Customers: [
    `CREATE INDEX IF NOT EXISTS idx_customers_name   ON "Customers" ("NameShort")`,
    `CREATE INDEX IF NOT EXISTS idx_customers_inn    ON "Customers" ("INN") WHERE "INN" IS NOT NULL AND "INN" != ''`,
    `CREATE INDEX IF NOT EXISTS idx_customers_tariff ON "Customers" ("ID_Tariff")`,
  ],

  Tariffs: [
    `CREATE INDEX IF NOT EXISTS idx_tariffs_name ON "Tariffs" ("Name")`,
  ],

  Bosses: [
    `CREATE INDEX IF NOT EXISTS idx_bosses_name ON "Bosses" ("Name")`,
  ],

  Cargos: [
    `CREATE INDEX IF NOT EXISTS idx_cargos_name ON "Cargos" ("Name")`,
  ],

  Cars: [
    `CREATE UNIQUE INDEX IF NOT EXISTS idx_cars_nick          ON "Cars" ("Nick")`,
    `CREATE INDEX IF NOT EXISTS       idx_cars_id_boss        ON "Cars" ("ID_Boss")`,
    `CREATE INDEX IF NOT EXISTS       idx_cars_regnogos       ON "Cars" ("RegNoGos")`,
    `CREATE INDEX IF NOT EXISTS       idx_cars_driver1        ON "Cars" ("ID_Driver1")`,
    `CREATE INDEX IF NOT EXISTS       idx_cars_driver2        ON "Cars" ("ID_Driver2") WHERE "ID_Driver2" IS NOT NULL`,
    `CREATE INDEX IF NOT EXISTS       idx_cars_cartype        ON "Cars" ("ID_CarType")`,
    `CREATE INDEX IF NOT EXISTS       idx_cars_pointtype      ON "Cars" ("ID_PointType")`,
    `CREATE INDEX IF NOT EXISTS       idx_cars_isref          ON "Cars" ("IsRef")`
  ],

  xSaldo: [
    `CREATE INDEX IF NOT EXISTS idx_xsaldo_main ON "xSaldo" ("ID_CustomerPay", "ID_Boss")`,
    `CREATE INDEX IF NOT EXISTS idx_xsaldo_cash ON "xSaldo" ("IsCash")`,
  ],
};

// Порядок создания таблиц (важно для внешних ключей в будущем)
export const TablesOrder = [
  "Bosses",
  "Cargos",
  "Cars",
  "Customers",
  "Tariffs",
  "xSaldo",
  "Applications",
  "XInvoices",
  "XInvoicePays",
  "XInvoiceDatas"
];