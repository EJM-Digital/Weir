import { connectionRequestCases } from "../../test/wire/connections.cases";
import { homeNetworkRequestCases } from "../../test/wire/home-network.cases";
import { activityRequestCases } from "../../test/wire/files.cases";
import { describeRequests } from "../../test/wire/request-case";

// The workflow, library and rule requests are described in lib/processing/library-requests.test.ts, beside the code
// the web-processing-libraries mutation run changes and the tests it runs.
describeRequests("Activity requests", activityRequestCases);
describeRequests("Connection and maintenance requests", connectionRequestCases);
describeRequests("Home network sign-in requests", homeNetworkRequestCases);
