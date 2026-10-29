import type { components } from "./generated/openapi-types";

type SchemaName = keyof components["schemas"];

/** A type from the server's OpenAPI document, by schema name. */
export type Schema<T extends SchemaName> = components["schemas"][T];

/** A request body as the caller builds it: the API function adds the CSRF token itself. */
export type RequestBody<T extends SchemaName> = Omit<Schema<T>, "csrf_token">;

export type UserPublic = Schema<"UserPublic">;
export type CurrentSession = Schema<"CurrentSessionOut">;
export type ActiveSession = Schema<"SessionOut">;
export type SessionAction = Schema<"SessionActionOut">;
export type BootstrapStatus = Schema<"BootstrapStatusOut">;
export type BootstrapResult = Schema<"BootstrapOut">;
export type RecoveryCodesStatus = Schema<"RecoveryCodesStatusOut">;
export type NewRecoveryCodes = Schema<"RecoveryCodesNewSetOut">;
export type ConfirmedRecoveryCodes = Schema<"RecoveryCodesConfirmOut">;
export type SpentRecoveryCode = Schema<"RecoveryCodeOut">;
export type RecoveryPasswordResult = Schema<"RecoveryPasswordOut">;
export type ActivityEventItem = Schema<"ActivityEventItemOut">;
export type SystemReadiness = Schema<"ReadinessResponse">;
export type ActivityRecentResponse = Schema<"ActivityRecentOut">;
