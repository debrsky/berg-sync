import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { SchemaConfig, IndexesConfig, TablesOrder } from "./schema.config.mjs";

const directory = path.dirname(fileURLToPath(import.meta.url));
const output = path.join(directory, "migration-schema.json");

fs.writeFileSync(
  output,
  JSON.stringify(
    {
      tablesOrder: TablesOrder,
      tables: SchemaConfig,
      indexes: IndexesConfig,
    },
    null,
    2,
  ) + "\n",
);

console.log(`Создан ${output}`);
