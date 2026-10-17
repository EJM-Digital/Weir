import openapiText from "../../openapi/weir-openapi.json?raw";

type JsonSchema = {
  $ref?: string;
  type?: string | string[];
  const?: unknown;
  enum?: unknown[];
  anyOf?: JsonSchema[];
  oneOf?: JsonSchema[];
  allOf?: JsonSchema[];
  items?: JsonSchema;
  properties?: Record<string, JsonSchema>;
  required?: string[];
  additionalProperties?: boolean | JsonSchema;
};

type Operation = {
  responses?: Record<
    string,
    { content?: { "application/json"?: { schema?: JsonSchema } } }
  >;
};

type Document = {
  paths: Record<string, Record<string, Operation>>;
  components: { schemas: Record<string, JsonSchema> };
};

const document = JSON.parse(openapiText) as Document;

function resolve(schema: JsonSchema): JsonSchema {
  if (!schema.$ref) return schema;
  const name = schema.$ref.replace("#/components/schemas/", "");
  const target = document.components.schemas[name];
  if (!target)
    throw new Error(`The schema ${schema.$ref} is not in the document.`);
  return target;
}

function kindOf(value: unknown): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  return typeof value;
}

function matchesType(type: string, value: unknown): boolean {
  const kind = kindOf(value);
  if (type === "integer") return Number.isInteger(value);
  if (type === "number") return kind === "number";
  return type === kind;
}

/** What is wrong with `value` against `schema`, one sentence each, naming where in the body it is. */
export function problemsWith(
  schema: JsonSchema,
  value: unknown,
  where = "body",
): string[] {
  const here = resolve(schema);
  const alternatives = here.anyOf ?? here.oneOf;
  if (alternatives) {
    const tries = alternatives.map((alternative) =>
      problemsWith(alternative, value, where),
    );
    return tries.some((found) => found.length === 0)
      ? []
      : [
          `${where}: matches none of ${alternatives.length} alternatives (${tries.map((found) => found[0]).join(" | ")})`,
        ];
  }
  const problems = (here.allOf ?? []).flatMap((part) =>
    problemsWith(part, value, where),
  );
  if ("const" in here && value !== here.const) {
    problems.push(`${where}: expected ${JSON.stringify(here.const)}`);
  }
  if (here.enum && !here.enum.includes(value)) {
    problems.push(
      `${where}: ${JSON.stringify(value)} is not one of ${JSON.stringify(here.enum)}`,
    );
  }
  const types = here.type === undefined ? [] : [here.type].flat();
  if (types.length > 0 && !types.some((type) => matchesType(type, value))) {
    problems.push(
      `${where}: expected ${types.join(" or ")}, got ${kindOf(value)}`,
    );
    return problems;
  }
  if (Array.isArray(value) && here.items) {
    value.forEach((item, index) =>
      problems.push(...problemsWith(here.items!, item, `${where}[${index}]`)),
    );
  }
  if (kindOf(value) === "object" && here.properties) {
    const object = value as Record<string, unknown>;
    for (const key of here.required ?? []) {
      if (!(key in object)) problems.push(`${where}: ${key} is missing`);
    }
    for (const [key, inner] of Object.entries(object)) {
      const declared = here.properties[key];
      if (declared) {
        problems.push(...problemsWith(declared, inner, `${where}.${key}`));
      } else if (
        here.additionalProperties === undefined ||
        here.additionalProperties === false
      ) {
        problems.push(`${where}: ${key} is not in the schema`);
      } else if (typeof here.additionalProperties === "object") {
        problems.push(
          ...problemsWith(here.additionalProperties, inner, `${where}.${key}`),
        );
      }
    }
  }
  return problems;
}

/** The schema the server's document gives the body of `METHOD /path` answered with `status`, or undefined when it gives none. */
export function documentedReply(
  method: string,
  path: string,
  status: number,
): JsonSchema | undefined {
  for (const [template, operations] of Object.entries(document.paths)) {
    const pattern = new RegExp(`^${template.replace(/\{[^}]+\}/g, "[^/]+")}$`);
    if (!pattern.test(path)) continue;
    const reply = operations[method.toLowerCase()]?.responses?.[String(status)];
    return reply?.content?.["application/json"]?.schema;
  }
  return undefined;
}
