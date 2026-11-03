/** RFC 4180 records; null for a record that ends inside an open quote. Blank lines are skipped. */
export function parseCsv(text: string): (string[] | null)[] {
  const source = text.startsWith("﻿") ? text.slice(1) : text;
  const records: (string[] | null)[] = [];
  let fields: string[] = [];
  let field = "";
  let quoted = false;
  let i = 0;
  const endRecord = () => {
    fields.push(field);
    if (fields.length > 1 || fields[0] !== "") records.push(fields);
    fields = [];
    field = "";
  };
  while (i < source.length) {
    const c = source[i]!;
    if (quoted) {
      if (c === '"' && source[i + 1] === '"') {
        field += '"';
        i += 2;
        continue;
      }
      if (c === '"') quoted = false;
      else field += c;
      i++;
      continue;
    }
    if (c === '"' && field === "") quoted = true;
    else if (c === ",") {
      fields.push(field);
      field = "";
    } else if (c === "\n" || c === "\r") {
      if (c === "\r" && source[i + 1] === "\n") i++;
      endRecord();
    } else field += c;
    i++;
  }
  if (quoted) records.push(null);
  else if (field !== "" || fields.length) endRecord();
  return records;
}
