using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// A finished run is green only if it executed a test and none failed. With domain reload
    /// disabled, every PlayMode run after the first used to load no test assemblies and come
    /// back "succeeded" with zero tests; a filter that matched nothing read the same way.
    /// </summary>
    public class TestJobResultTests
    {
        private const string JobId = "test-job-result-zero";

        [Test]
        public void ARunThatExecutedNothingFails()
        {
            TestJob job = Finish(Result(passed: 0, failed: 0, skipped: 0));

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            StringAssert.StartsWith("no_tests_executed:", job.Error);
        }

        [Test]
        public void ARunThatOnlySkippedFails()
        {
            TestJob job = Finish(Result(passed: 0, failed: 0, skipped: 3));

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            StringAssert.StartsWith("no_tests_executed:", job.Error);
            StringAssert.Contains("3 skipped", job.Error);
        }

        [Test]
        public void ARunWithAPassAndNoFailureSucceeds()
        {
            TestJob job = Finish(Result(passed: 1, failed: 0, skipped: 2));

            Assert.AreEqual(TestJobStatus.Succeeded, job.Status);
            Assert.IsNull(job.Error);
        }

        [Test]
        public void ARunWithAFailureFails()
        {
            TestJob job = Finish(Result(passed: 4, failed: 1, skipped: 0));

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.IsNull(job.Error, "a failing test explains itself");
        }

        [Test]
        public void ARunWithoutAResultFails()
        {
            TestJob job = Finish(null);

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.IsNotNull(job.Error);
        }

        /// <summary>The RunFinished callback is the path every real run finalizes through.</summary>
        [Test]
        public void TheRunFinishedPathFailsAnEmptyRunToo()
        {
            Type manager = typeof(TestJobManager);
            var jobs = (IDictionary)manager.GetField("Jobs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            FieldInfo current = manager.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            object originalCurrent = current.GetValue(null);
            var job = new TestJob
            {
                JobId = JobId,
                Status = TestJobStatus.Running,
                Mode = "PlayMode",
                FailuresSoFar = new List<TestJobFailure>(),
            };

            try
            {
                jobs[JobId] = job;
                current.SetValue(null, JobId);

                TestJobManager.FinalizeCurrentJobFromRunFinished(Result(passed: 0, failed: 0, skipped: 0));

                Assert.AreEqual(TestJobStatus.Failed, job.Status);
                StringAssert.StartsWith("no_tests_executed:", job.Error);
                Assert.AreEqual(0, job.Summary.Total);
                Assert.IsNull(current.GetValue(null), "the job is no longer current");
            }
            finally
            {
                jobs.Remove(JobId);
                current.SetValue(null, originalCurrent);
                manager.GetMethod("PersistToSessionState", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { true });
            }
        }

        private static TestJob Finish(TestRunResult result)
        {
            var job = new TestJob { JobId = JobId, Status = TestJobStatus.Running };
            TestJobManager.ApplyRunResult(job, result);
            Assert.AreSame(result, job.Result);
            return job;
        }

        private static TestRunResult Result(int passed, int failed, int skipped) =>
            new TestRunResult(
                new TestRunSummary(passed + failed + skipped, passed, failed, skipped, 0.1, "Passed"),
                new List<TestRunTestResult>());
    }
}
