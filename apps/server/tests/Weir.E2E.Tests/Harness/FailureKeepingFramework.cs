using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Weir.E2E.Tests.Harness.FailureKeepingFramework", "Weir.E2E.Tests")]

namespace Weir.E2E.Tests.Harness;

/// <summary>
/// xunit's own framework, watching the results go by: a test that failed keeps the evidence it wrote
/// (<see cref="FailureArtifacts"/>) and a test that did not has it deleted. The evidence is written while the test ends, so it
/// is there before the result is reported.
/// </summary>
public sealed class FailureKeepingFramework(IMessageSink diagnosticMessageSink) : XunitTestFramework(diagnosticMessageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(
            IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new XunitTestAssemblyRunner(
                TestAssembly, testCases, DiagnosticMessageSink, new KeepingSink(executionMessageSink), executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class KeepingSink(IMessageSink inner) : LongLivedMarshalByRefObject, IMessageSink
    {
        public bool OnMessage(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestFailed failed:
                    FailureArtifacts.Keep(KeyOf(failed.TestCase), ErrorOf(failed));
                    break;
                case ITestPassed passed:
                    FailureArtifacts.Discard(KeyOf(passed.TestCase));
                    break;
                case ITestSkipped skipped:
                    FailureArtifacts.Discard(KeyOf(skipped.TestCase));
                    break;
            }

            return inner.OnMessage(message);
        }

        private static string KeyOf(ITestCase testCase) =>
            FailureArtifacts.KeyOf(testCase.TestMethod.TestClass.Class.Name.Split('.').Last(), testCase.TestMethod.Method.Name);

        private static string ErrorOf(ITestFailed failed) =>
            $"{failed.Test.DisplayName}{Environment.NewLine}{Environment.NewLine}" +
            string.Join(
                Environment.NewLine + Environment.NewLine,
                failed.ExceptionTypes.Select((type, index) => $"{type}: {failed.Messages[index]}{Environment.NewLine}{failed.StackTraces[index]}"));
    }
}
