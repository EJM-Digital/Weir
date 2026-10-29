import { connectionRequestCases } from "../../test/wire/connections.cases";
import { homeNetworkRequestCases } from "../../test/wire/home-network.cases";
import { activityRequestCases } from "../../test/wire/files.cases";
import { workflowRequestCases } from "../../test/wire/libraries.cases";
import { describeRequests } from "../../test/wire/request-case";
import { ruleRequestCases } from "../../test/wire/rule-sets.cases";

describeRequests("Activity requests", activityRequestCases);
describeRequests("Workflow and library requests", workflowRequestCases);
describeRequests("Rule requests", ruleRequestCases);
describeRequests("Connection and maintenance requests", connectionRequestCases);
describeRequests("Home network sign-in requests", homeNetworkRequestCases);
