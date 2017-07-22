﻿using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using AshMind.Extensions;
using Pedantic.IO;
using MirrorSharp;
using MirrorSharp.Testing;
using SharpLab.Server;
using SharpLab.Server.MirrorSharp.Internal;
using System;
using MirrorSharp.Testing.Results;

namespace SharpLab.Tests {
    public class ExecutionTests {
        private static readonly MirrorSharpOptions MirrorSharpOptions = Startup.CreateMirrorSharpOptions();

        [Theory]
        [InlineData("Exception.DivideByZero.cs", 4, "DivideByZeroException")]
        [InlineData("Exception.DivideByZero.Catch.cs", 5, "DivideByZeroException")]
        [InlineData("Exception.DivideByZero.Catch.When.True.cs", 5, "DivideByZeroException")]
        [InlineData("Exception.DivideByZero.Catch.When.False.cs", 5, "DivideByZeroException")]
        [InlineData("Exception.DivideByZero.Finally.cs", 5, "DivideByZeroException")]
        public async Task SlowUpdate_ReportsExceptionInFlow(string resourceName, int expectedLineNumber, string expectedExceptionTypeName) {
            var driver = await NewTestDriverAsync(LoadCodeFromResource(resourceName));

            var result = await driver.SendSlowUpdateAsync<ExecutionResultData>();
            var steps = result.ExtensionResult.Flow
                .Select(s => new { s.Line, s.Exception })
                .ToArray();

            AssertIsSuccess(result, allowRuntimeException: true);
            Assert.Contains(new { Line = expectedLineNumber, Exception = expectedExceptionTypeName }, steps);
        }

        [Theory]
        [InlineData("Loop.For.10Iterations.cs", 3, "i: 0; i: 1; i: 2; …")]
        [InlineData("Variable.MultipleDeclarationsOnTheSameLine.cs", 3, "a: 0, b: 0, c: 0, …")]
        [InlineData("Variable.LongName.cs", 3, "abcdefghi…: 0")]
        [InlineData("Variable.LongValue.cs", 3, "x: 123456789…")]
        public async Task SlowUpdate_ReportsVariableNotesWithLengthLimits(string resourceName, int lineNumber, string expectedNotes) {
            var driver = await NewTestDriverAsync(LoadCodeFromResource(resourceName));

            var result = await driver.SendSlowUpdateAsync<ExecutionResultData>();

            var notes = string.Join(
                "; ",
                result.ExtensionResult.Flow
                    .Where(s => s.Line == lineNumber && s.Notes != null)
                    .Select(s => s.Notes)
            );

            AssertIsSuccess(result);
            Assert.Equal(expectedNotes, notes);
        }

        [Theory]
        [InlineData("3.Inspect();", "Inspect: 3")]
        [InlineData("(1, 2, 3).Inspect();", "Inspect: (1, 2, 3)")]
        [InlineData("new[] { 1, 2, 3 }.Inspect();", "Inspect: { 1, 2, 3 }")]
        [InlineData("3.Dump();", "Dump: 3")]
        public async Task SlowUpdate_IncludesExpectedInspectAndDumpOutput(string code, string expectedOutput) {
            var driver = await NewTestDriverAsync(@"
                public class C {
                    public void M() { " + code + @" }
                }
            ");

            var result = await driver.SendSlowUpdateAsync<ExecutionResultData>();

            AssertIsSuccess(result);
            Assert.Equal(expectedOutput, result.ExtensionResult.GetOutputAsString());
        }

        [Theory]
        [InlineData("Console.Write(\"abc\");", "abc")]
        [InlineData("Console.WriteLine(\"abc\");", "abc{newline}")]
        [InlineData("Console.Write('a');", "a")]
        [InlineData("Console.Write(3);", "3")]
        [InlineData("Console.Write(3.1);", "3.1")]
        [InlineData("Console.Write(new object());", "System.Object")]
        public async Task SlowUpdate_IncludesConsoleOutput(string code, string expectedOutput) {
            var driver = await NewTestDriverAsync(@"
                using System;
                public class C {
                    public void M() { " + code + @" }
                }
            ");

            var result = await driver.SendSlowUpdateAsync<ExecutionResultData>();

            AssertIsSuccess(result);
            Assert.Equal(
                expectedOutput.Replace("{newline}", Environment.NewLine),
                result.ExtensionResult.GetOutputAsString()
            );
        }

        private static void AssertIsSuccess(SlowUpdateResult<ExecutionResultData> result, bool allowRuntimeException = false) {
            var errors = result.JoinErrors();
            Assert.True(errors.IsNullOrEmpty(), errors);
            if (allowRuntimeException)
                return;
            Assert.Equal(null, result.ExtensionResult.Exception);
        }

        private static string LoadCodeFromResource(string resourceName) {
            return EmbeddedResource.ReadAllText(typeof(ExecutionTests), "TestCode.Execution." + resourceName);
        }

        private static async Task<MirrorSharpTestDriver> NewTestDriverAsync(string code) {
            var driver = MirrorSharpTestDriver.New(MirrorSharpOptions).SetText(code);
            await driver.SendSetOptionsAsync(new Dictionary<string, string> {
                { "optimize", "debug" },
                { "x-target", TargetNames.Run }
            });
            return driver;
        }

        private class ExecutionResultData {
            public string Exception { get; set; }
            [JsonIgnore]
            public IList<FlowStepData> Flow { get; } = new List<FlowStepData>();
            [JsonProperty("flow")]
            private IList<JToken> FlowRaw { get; } = new List<JToken>();
            [JsonProperty]
            private IList<JToken> Output { get; } = new List<JToken>();

            public string GetOutputAsString() {
                return string.Join("\n", Output.Select(token => {
                    if (token is JObject @object)
                        return @object.Value<string>("title") + ": " + @object.Value<string>("value");
                    return token.Value<string>();
                }));
            }

            //public string GetOutputAsString() {
            //    var valueGroupKey = new object();
            //    var groups = Output.GroupAdjacentBy(token => (token is JValue) ? valueGroupKey : token);

            //    return string.Join("\n", groups.Select(g => string.Join("", g.Select(token => {
            //        if (token is JObject @object)
            //            return @object.Value<string>("title") + ": " + @object.Value<string>("value");
            //        return token.Value<string>();
            //    }))));
            //}

            [OnDeserialized]
            private void OnDeserialized(StreamingContext context) {
                foreach (var token in FlowRaw) {
                    Flow.Add(ParseStepData(token));
                }
            }

            private FlowStepData ParseStepData(JToken token) {
                if (token is JValue value)
                    return new FlowStepData { Line = value.Value<int>() };

                return new FlowStepData {
                    Line = token.Value<int>("line"),
                    Exception = token.Value<string>("exception"),
                    Notes = token.Value<string>("notes")
                };
            }
        }

        private class FlowStepData {
            public int Line { get; set; }
            public string Exception { get; set; }
            public string Notes { get; set; }
        }
    }
}
