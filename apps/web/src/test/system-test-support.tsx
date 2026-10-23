import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";

import { useAppSettingsQuery } from "../lib/settings/queries";
import type { AppSettings } from "../lib/settings/types";
import { systemReplies } from "./recorded-system";
import type { RecordedRoutes } from "./recorded-weir";
import { csrfToken } from "../pages/system/tabs/security/security-replies";
import {
  useSystemSettingsForm,
  type SystemSettingsForm,
} from "../pages/system/use-system-settings-form";

/** What every System save asks first: a fresh confirmation token, then the change itself. */
export const SAVING: RecordedRoutes = {
  "GET /api/v1/auth/csrf": csrfToken,
};

/** The settings every System form edits, read as the server sends them. */
export const SETTINGS: RecordedRoutes = {
  "GET /api/v1/suite/settings": systemReplies.settings,
};

/** The token the recorded confirmation reply carries, which every change of the server's settings sends back. */
export const CSRF_TOKEN = (JSON.parse(csrfToken.body) as { csrf_token: string })
  .csrf_token;

/** The settings the server sent back, as the page's drafts start from. */
export const SAVED_SETTINGS = JSON.parse(
  systemReplies.settings.body,
) as AppSettings;

function WithSettingsForm({
  children,
}: {
  children: (form: SystemSettingsForm, settings: AppSettings) => ReactNode;
}) {
  const settings = useAppSettingsQuery();
  const form = useSystemSettingsForm(settings.data);
  return settings.data ? <>{children(form, settings.data)}</> : null;
}

/**
 * A System tab's card with the form it edits: the real settings read, the real drafts and the real save, shown once the
 * settings have been read. The client asks again on every mount and never retries, so a test sees every request.
 */
export function renderWithSettingsForm(
  children: (form: SystemSettingsForm, settings: AppSettings) => ReactNode,
) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  return {
    client,
    ...render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <WithSettingsForm>{children}</WithSettingsForm>
        </MemoryRouter>
      </QueryClientProvider>,
    ),
  };
}
