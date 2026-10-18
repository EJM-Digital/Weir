import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, renderHook, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AppSettings } from "../../../../lib/settings/types";
import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  CSRF_TOKEN,
  renderWithSettingsForm,
  SAVED_SETTINGS,
  SAVING,
} from "../../system-test-support";
import { useSystemSettingsForm } from "../../use-system-settings-form";
import { RetentionSection } from "./retention-section";
import { useFileActivityRetention } from "./use-file-activity-retention";

const SETTINGS = "GET /api/v1/suite/settings";
const SAVE = "PUT /api/v1/suite/settings";
const SAVE_PATH = "/api/v1/suite/settings";

/** The settings of a server that keeps the log 30 days (and Events 90), and of one that keeps it 3650 (and Events until cleared). */
const THIRTY: RecordedReply = systemReplies.settings;
const MOST: RecordedReply = systemReplies.settingsPutRetention;

function Retention({
  form,
}: {
  form: ReturnType<typeof useSystemSettingsForm>;
}) {
  const fileActivity = useFileActivityRetention();
  return <RetentionSection form={form} fileActivity={fileActivity} editable />;
}

function renderRetention(routes: RecordedRoutes): RecordedWeir {
  const weir = serveRecordedReplies({
    ...SAVING,
    [SETTINGS]: THIRTY,
    "GET /api/v1/processing/operator-settings": systemReplies.operatorSettings,
    ...routes,
  });
  renderWithSettingsForm((form) => <Retention form={form} />);
  return weir;
}

/** What the page sent to save the settings. */
const savedBodies = (weir: RecordedWeir) =>
  weir.sent("PUT", SAVE_PATH).map((request) => request.body as AppSettings);

const field = (label: string) => screen.findByLabelText(label);

/** Types into System log the way a person does: click in, change the number, click out. */
async function typeLogDays(value: string): Promise<HTMLElement> {
  const input = await field("System log");
  fireEvent.focus(input);
  fireEvent.change(input, { target: { value } });
  fireEvent.blur(input);
  return input;
}

afterEach(() => vi.unstubAllGlobals());

describe("System log days", () => {
  it.each([
    {
      label: "a number in range",
      start: THIRTY,
      typed: "45",
      shown: 45,
      sent: 45,
    },
    {
      label: "the longest, 3650",
      start: THIRTY,
      typed: "3650",
      shown: 3650,
      sent: 3650,
    },
    {
      label: "more than the longest",
      start: THIRTY,
      typed: "99999",
      shown: 3650,
      sent: 3650,
    },
    { label: "the shortest, 1", start: MOST, typed: "1", shown: 1, sent: 1 },
    { label: "0", start: MOST, typed: "0", shown: 1, sent: 1 },
    { label: "a negative number", start: MOST, typed: "-5", shown: 1, sent: 1 },
    {
      label: "part of a day",
      start: THIRTY,
      typed: "12.7",
      shown: 12,
      sent: 12,
    },
    { label: "nothing at all", start: MOST, typed: "", shown: 30, sent: 30 },
  ])(
    '$label: typing "$typed" shows $shown and saves $sent days',
    async ({ start, typed, shown, sent }) => {
      const weir = renderRetention({
        [SETTINGS]: start,
        [SAVE]: systemReplies.settingsPutRetentionOne,
      });

      const input = await typeLogDays(typed);

      expect(input).toHaveValue(shown);
      fireEvent.click(await screen.findByTestId("suite-settings-save-logs"));
      await screen.findByText("Retention saved.");
      expect(savedBodies(weir)).toEqual([
        {
          setup_wizard_state: SAVED_SETTINGS.setup_wizard_state,
          app_timezone: SAVED_SETTINGS.app_timezone,
          log_retention_days: sent,
          activity_retention_days: (JSON.parse(start.body) as AppSettings)
            .activity_retention_days,
          configuration_backup_enabled:
            SAVED_SETTINGS.configuration_backup_enabled,
          configuration_backup_interval_hours:
            SAVED_SETTINGS.configuration_backup_interval_hours,
          configuration_backup_preferred_time:
            SAVED_SETTINGS.configuration_backup_preferred_time,
          csrf_token: CSRF_TOKEN,
        },
      ]);
    },
  );

  it("offers no Save when the number is put back to what is kept", async () => {
    renderRetention({});

    await typeLogDays("45");
    expect(screen.getByTestId("suite-settings-save-logs")).toBeVisible();
    await typeLogDays("30");

    expect(
      screen.queryByTestId("suite-settings-save-logs"),
    ).not.toBeInTheDocument();
  });
});

describe("Events days", () => {
  it("keeps 0, which keeps events until they are cleared", async () => {
    const weir = renderRetention({
      [SETTINGS]: [THIRTY, MOST],
      [SAVE]: MOST,
    });

    const input = await field("Events");
    fireEvent.change(input, { target: { value: "0" } });
    fireEvent.blur(input);
    fireEvent.click(await screen.findByTestId("suite-settings-save-logs"));

    await screen.findByText("Retention saved.");
    expect(savedBodies(weir)[0]).toMatchObject({ activity_retention_days: 0 });
    expect(input).toHaveValue(0);
  });

  it("brings a number above 3650 down to 3650 and a negative one up to 0", async () => {
    renderRetention({});
    const input = await field("Events");

    fireEvent.change(input, { target: { value: "99999" } });
    fireEvent.blur(input);
    expect(input).toHaveValue(3650);
    fireEvent.change(input, { target: { value: "-4" } });
    fireEvent.blur(input);
    expect(input).toHaveValue(0);
  });
});

describe("Retention Save", () => {
  it("says Saving… while the server has the change, then Retention saved.", async () => {
    let answer: (reply: RecordedReply) => void = () => undefined;
    renderRetention({
      [SAVE]: () =>
        new Promise<RecordedReply>((resolve) => {
          answer = resolve;
        }),
    });

    await typeLogDays("45");
    fireEvent.click(await screen.findByTestId("suite-settings-save-logs"));

    expect(
      await screen.findByRole("button", { name: "Saving…" }),
    ).toBeDisabled();
    expect(screen.queryByText("Retention saved.")).toBeNull();
    answer(systemReplies.settingsPutRetentionOne);
    expect(await screen.findByText("Retention saved.")).toBeVisible();
  });

  it("says the retention could not be saved and keeps Save for another try", async () => {
    renderRetention({ [SAVE]: systemReplies.settingsPutFails });

    await typeLogDays("45");
    fireEvent.click(await screen.findByTestId("suite-settings-save-logs"));

    expect(
      await screen.findByTestId("suite-settings-logs-save-error"),
    ).toHaveTextContent("Could not save settings.");
    expect(screen.getByTestId("suite-settings-save-logs")).toBeEnabled();
    expect(screen.queryByText("Retention saved.")).toBeNull();
  });
});

describe("a System log number that is not a number", () => {
  function formFor(settings: AppSettings) {
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    return renderHook(() => useSystemSettingsForm(settings), { wrapper });
  }

  it("keeps the number that is saved, not the default", () => {
    const { result } = formFor(JSON.parse(MOST.body) as AppSettings);

    act(() => result.current.retention.setLogDraft("abc"));

    expect(result.current.retention.finalizeLogDays()).toBe(3650);
  });

  it("keeps the number that is saved for an Events number that is not a number", () => {
    const { result } = formFor(JSON.parse(MOST.body) as AppSettings);

    act(() => result.current.retention.setActivityDraft("abc"));

    expect(result.current.retention.finalizeActivityDays()).toBe(0);
  });
});
