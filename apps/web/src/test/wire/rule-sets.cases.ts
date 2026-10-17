import { recordedBody } from "../recorded";
import {
  createProcessingRuleSet,
  deleteProcessingRuleSet,
  fetchProcessingRuleSets,
  updateProcessingRuleSet,
  writeFromProcessingRuleSet,
  type ProcessingRuleSet,
} from "../../lib/processing/rule-sets-api";
import {
  fetchDirectPlayDevices,
  putDirectPlayDevices,
} from "../../lib/processing/direct-play-api";
import type { RequestCase } from "./request-case";

const RULE_SETS = "/api/v1/processing/rule-sets";
const MOVIES_PROFILE = writeFromProcessingRuleSet(
  recordedBody<ProcessingRuleSet[]>(`GET ${RULE_SETS}`)[0]!,
);
const NEW_PROFILE = {
  ...MOVIES_PROFILE,
  name: "Recorded profile",
  primary_audio_lang: "fra",
  secondary_audio_lang: "eng",
  subtitle_mode: "keep_listed",
  subtitle_langs_csv: "eng,fra",
};

/** The requests behind Setup › Rules: the profiles, and the devices that Direct Play answers for. */
export const ruleRequestCases: RequestCase[] = [
  {
    fn: fetchProcessingRuleSets,
    asking: "the profiles",
    call: () => fetchProcessingRuleSets(),
    method: "GET",
    path: RULE_SETS,
    reply: `GET ${RULE_SETS}`,
  },
  {
    fn: createProcessingRuleSet,
    asking: "creating a profile with every rule of the one it started from",
    call: () => createProcessingRuleSet(NEW_PROFILE),
    method: "POST",
    path: RULE_SETS,
    body: NEW_PROFILE,
    reply: `POST ${RULE_SETS}`,
  },
  {
    fn: updateProcessingRuleSet,
    asking: "saving a profile",
    call: () =>
      updateProcessingRuleSet(3, {
        ...MOVIES_PROFILE,
        name: "Recorded profile",
        remove_commentary: false,
      }),
    method: "PUT",
    path: `${RULE_SETS}/3`,
    body: {
      ...MOVIES_PROFILE,
      name: "Recorded profile",
      remove_commentary: false,
    },
    reply: `PUT ${RULE_SETS}/3`,
  },
  {
    fn: deleteProcessingRuleSet,
    asking: "removing a profile no workflow uses",
    call: () => deleteProcessingRuleSet(3),
    method: "DELETE",
    path: `${RULE_SETS}/3`,
    body: {},
    reply: `DELETE ${RULE_SETS}/3`,
  },
  {
    fn: deleteProcessingRuleSet,
    asking:
      "removing a profile a workflow still uses, which the server refuses",
    call: () => deleteProcessingRuleSet(1),
    method: "DELETE",
    path: `${RULE_SETS}/1`,
    body: {},
    reply: `DELETE ${RULE_SETS}/1 (in use)`,
  },
  {
    fn: fetchDirectPlayDevices,
    asking: "the devices Direct Play can answer for",
    call: () => fetchDirectPlayDevices(),
    method: "GET",
    path: "/api/v1/processing/direct-play/devices",
    reply: "GET /api/v1/processing/direct-play/devices",
  },
  {
    fn: putDirectPlayDevices,
    asking: "choosing the devices",
    call: () => putDirectPlayDevices(["apple_tv_4k"]),
    method: "PUT",
    path: "/api/v1/processing/direct-play/devices",
    body: { selected: ["apple_tv_4k"] },
    reply: "PUT /api/v1/processing/direct-play/devices",
  },
];
