using System.Collections.Concurrent;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Weir.Contract.Tests.Harness.FailureKeepingFramework", "Weir.Contract.Tests")]

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// xunit's own framework, with two additions. Each test case, and each class's setup of its shared server, runs with
/// <see cref="FailureEvidence.CurrentName"/> set, so a server started there is kept under that name. And the results are watched
/// as they go by: a test that failed keeps what its servers logged (<see cref="FailureEvidence"/>), and a test that did not has it
/// deleted. The servers copy their logs as they stop, which is before the result is reported.
/// </summary>
public sealed class FailureKeepingFramework(IMessageSink diagnosticMessageSink) : XunitTestFramework(diagnosticMessageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private static string NameOf(ITestCase testCase) =>
        FailureEvidence.NameOfTest(
            testCase.TestMethod.TestClass.Class.Name, testCase.TestMethod.Method.Name, testCase.DisplayName, testCase.UniqueID);

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(
            IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new AssemblyRunner(
                TestAssembly, testCases, DiagnosticMessageSink, new KeepingSink(executionMessageSink), executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class AssemblyRunner(
        ITestAssembly testAssembly,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        protected override Task<RunSummary> RunTestCollectionAsync(
            IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource) =>
            new CollectionRunner(
                testCollection, testCases, DiagnosticMessageSink, messageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), cancellationTokenSource)
                .RunAsync();
    }

    private sealed class CollectionRunner(
        ITestCollection testCollection,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        ITestCaseOrderer testCaseOrderer,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
        : XunitTestCollectionRunner(testCollection, testCases, diagnosticMessageSink, messageBus, testCaseOrderer, aggregator, cancellationTokenSource)
    {
        protected override Task<RunSummary> RunTestClassAsync(ITestClass testClass, IReflectionTypeInfo @class, IEnumerable<IXunitTestCase> testCases) =>
            new ClassRunner(
                testClass, @class, testCases, DiagnosticMessageSink, MessageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator),
                CancellationTokenSource, CollectionFixtureMappings)
                .RunAsync();
    }

    private sealed class ClassRunner(
        ITestClass testClass,
        IReflectionTypeInfo @class,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        ITestCaseOrderer testCaseOrderer,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        IDictionary<Type, object> collectionFixtureMappings)
        : XunitTestClassRunner(testClass, @class, testCases, diagnosticMessageSink, messageBus, testCaseOrderer, aggregator, cancellationTokenSource, collectionFixtureMappings)
    {
        // The class's fixtures are made here, so a server they start is the class's.
        protected override async Task AfterTestClassStartingAsync()
        {
            FailureEvidence.CurrentName = FailureEvidence.NameOfSharedServer(TestClass.Class.Name);
            await base.AfterTestClassStartingAsync();
        }

        protected override Task<RunSummary> RunTestMethodAsync(
            ITestMethod testMethod, IReflectionMethodInfo method, IEnumerable<IXunitTestCase> testCases, object[] constructorArguments) =>
            new MethodRunner(
                testMethod, Class, method, testCases, DiagnosticMessageSink, MessageBus, new ExceptionAggregator(Aggregator), CancellationTokenSource,
                constructorArguments)
                .RunAsync();
    }

    private sealed class MethodRunner(
        ITestMethod testMethod,
        IReflectionTypeInfo @class,
        IReflectionMethodInfo method,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        object[] constructorArguments)
        : XunitTestMethodRunner(testMethod, @class, method, testCases, diagnosticMessageSink, messageBus, aggregator, cancellationTokenSource, constructorArguments)
    {
        // The test class is made, set up, run and disposed inside this call, so a server any of them starts is the test case's.
        protected override async Task<RunSummary> RunTestCaseAsync(IXunitTestCase testCase)
        {
            FailureEvidence.CurrentName = NameOf(testCase);
            return await base.RunTestCaseAsync(testCase);
        }
    }

    private sealed class KeepingSink(IMessageSink inner) : LongLivedMarshalByRefObject, IMessageSink
    {
        private readonly ConcurrentDictionary<string, List<string>> _failedByClass = new();

        public bool OnMessage(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestFailed failed:
                    FailureEvidence.Keep(NameOf(failed.TestCase), ErrorOf(failed));
                    _failedByClass.GetOrAdd(failed.TestCase.TestMethod.TestClass.Class.Name, _ => []).Add(failed.Test.DisplayName);
                    break;
                case ITestPassed passed:
                    FailureEvidence.Discard(NameOf(passed.TestCase));
                    break;
                case ITestSkipped skipped:
                    FailureEvidence.Discard(NameOf(skipped.TestCase));
                    break;
                case ITestClassFinished finished:
                    KeepOrDiscardSharedServer(finished.TestClass.Class.Name);
                    break;
            }

            return inner.OnMessage(message);
        }

        // The shared server's logs are the class's: kept when any of its tests failed, with the names of those tests.
        private void KeepOrDiscardSharedServer(string className)
        {
            var shared = FailureEvidence.NameOfSharedServer(className);
            if (_failedByClass.TryRemove(className, out var failed))
            {
                FailureEvidence.Keep(shared, "The tests of this class that failed, which used this server:" + Environment.NewLine + string.Join(Environment.NewLine, failed));
            }
            else
            {
                FailureEvidence.Discard(shared);
            }
        }

        private static string ErrorOf(ITestFailed failed) =>
            $"{failed.Test.DisplayName}{Environment.NewLine}{Environment.NewLine}" +
            string.Join(
                Environment.NewLine + Environment.NewLine,
                failed.ExceptionTypes.Select((type, index) => $"{type}: {failed.Messages[index]}{Environment.NewLine}{failed.StackTraces[index]}"));
    }
}
