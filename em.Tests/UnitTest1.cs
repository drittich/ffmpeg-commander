using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace em.Tests;

public sealed class CommandSessionTests
{
    private sealed class FakeCommandGenerator : global::em.CommandGenerator
    {
        public int GenerateCalls { get; private set; }
        public int AdjustCalls { get; private set; }

        public string? LastGenerateDescription { get; private set; }
        public string? LastAdjustPreviousFullCommand { get; private set; }
        public string? LastAdjustInstruction { get; private set; }

        public GeneratorCallResult GenerateResult { get; set; } = new GeneratorCallResult(" -i in.mp4 out.mp4", Error: null);
        public GeneratorCallResult AdjustResult { get; set; } = new GeneratorCallResult("-y -i in.mp4 out2.mp4", Error: null);

        public override Task<GeneratorCallResult> GenerateFromDescription(string description, CancellationToken ct = default)
        {
            GenerateCalls++;
            LastGenerateDescription = description;
            return Task.FromResult(GenerateResult);
        }

        public override Task<GeneratorCallResult> AdjustFromInstruction(string previousFullCommand, string instruction, CancellationToken ct = default)
        {
            AdjustCalls++;
            LastAdjustPreviousFullCommand = previousFullCommand;
            LastAdjustInstruction = instruction;
            return Task.FromResult(AdjustResult);
        }
    }

    private sealed class FakeCommandExecutor : global::em.CommandExecutor
    {
        public int ExecuteCalls { get; private set; }
        public string? LastExecutedCommand { get; private set; }

        public CommandExecutionResult Result { get; set; } = new CommandExecutionResult
        {
            ExitCode = 0,
            StdOut = "ok",
            StdErr = ""
        };

        public override Task<CommandExecutionResult> ExecuteAsync(string fullCommand, CancellationToken ct = default)
        {
            ExecuteCalls++;
            LastExecutedCommand = fullCommand;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task ApplyInputLine_FirstNaturalLanguageLine_StoresBaseRequestAndCommand()
    {
        var gen = new FakeCommandGenerator
        {
            GenerateResult = new global::em.CommandGenerator.GeneratorCallResult("-i in.mp4 out.mp4", Error: null)
        };
        var exec = new FakeCommandExecutor();
        var state = new global::em.CommandState();
        var session = new global::em.CommandSession(gen, exec, state);

        global::em.CommandSessionResult result = await session.ApplyInputLine("  ffmpeg convert this  ");

        Assert.False(result.ShutdownRequested);
        Assert.True(result.StateChanged);
        Assert.Equal("Command stored", result.Message);
        Assert.Same(session.State, result.State);

        Assert.Equal("convert this", session.State.BaseRequest);
        Assert.Equal("ffmpeg -i in.mp4 out.mp4", session.State.CurrentCommand);
        Assert.Empty(session.State.Adjustments);

        Assert.Null(session.State.LastOutput);
        Assert.Null(session.State.LastError);
        Assert.Null(session.State.LastExitCode);

        Assert.Equal(1, gen.GenerateCalls);
        Assert.Equal(0, gen.AdjustCalls);
        Assert.Equal(0, exec.ExecuteCalls);

        // Generator should receive the "description" portion (prefix stripped).
        Assert.Equal("convert this", gen.LastGenerateDescription);
    }

    [Fact]
    public async Task ApplyInputLine_SecondNaturalLanguageLine_AppendsAdjustmentAndUpdatesCommand()
    {
        var gen = new FakeCommandGenerator
        {
            GenerateResult = new global::em.CommandGenerator.GeneratorCallResult("-i in.mp4 out.mp4", Error: null),
            AdjustResult = new global::em.CommandGenerator.GeneratorCallResult("-y -i in.mp4 out2.mp4", Error: null)
        };
        var exec = new FakeCommandExecutor();
        var session = new global::em.CommandSession(gen, exec, new global::em.CommandState());

        await session.ApplyInputLine("make a clip");
        global::em.CommandSessionResult result = await session.ApplyInputLine("  add -y and change output  ");

        Assert.False(result.ShutdownRequested);
        Assert.True(result.StateChanged);
        Assert.Equal("Command updated", result.Message);

        Assert.Equal("make a clip", session.State.BaseRequest);
        Assert.Single(session.State.Adjustments);
        Assert.Equal("add -y and change output", session.State.Adjustments[0]);

        Assert.Equal("ffmpeg -y -i in.mp4 out2.mp4", session.State.CurrentCommand);
        Assert.Null(session.State.LastOutput);
        Assert.Null(session.State.LastExitCode);

        Assert.Equal(1, gen.GenerateCalls);
        Assert.Equal(1, gen.AdjustCalls);
        Assert.Equal("ffmpeg -i in.mp4 out.mp4", gen.LastAdjustPreviousFullCommand);
        Assert.Equal("add -y and change output", gen.LastAdjustInstruction);

        Assert.Equal(0, exec.ExecuteCalls);
    }

    [Fact]
    public async Task ApplyInputLine_Clear_ResetsState()
    {
        var gen = new FakeCommandGenerator();
        var exec = new FakeCommandExecutor();
        var state = new global::em.CommandState
        {
            BaseRequest = "req",
            CurrentCommand = "ffmpeg -i in.mp4 out.mp4",
            LastOutput = "stdout",
            LastError = "stderr",
            LastExitCode = 123
        };
        state.Adjustments.Add("adj1");

        var session = new global::em.CommandSession(gen, exec, state);

        global::em.CommandSessionResult result = await session.ApplyInputLine("clear");

        Assert.False(result.ShutdownRequested);
        Assert.True(result.StateChanged);
        Assert.Equal("Cleared", result.Message);

        Assert.Null(session.State.BaseRequest);
        Assert.Null(session.State.CurrentCommand);
        Assert.Empty(session.State.Adjustments);
        Assert.Null(session.State.LastOutput);
        Assert.Null(session.State.LastError);
        Assert.Null(session.State.LastExitCode);

        Assert.Equal(0, gen.GenerateCalls);
        Assert.Equal(0, gen.AdjustCalls);
        Assert.Equal(0, exec.ExecuteCalls);
    }

    [Fact]
    public async Task ApplyInputLine_RunWithNoCommand_WarnsAndDoesNothing()
    {
        var gen = new FakeCommandGenerator();
        var exec = new FakeCommandExecutor();
        var state = new global::em.CommandState { CurrentCommand = null };
        var session = new global::em.CommandSession(gen, exec, state);

        global::em.CommandSessionResult result = await session.ApplyInputLine("run");

        Assert.False(result.ShutdownRequested);
        Assert.False(result.StateChanged);
        Assert.Equal("No command to run", result.Message);

        Assert.Equal(0, exec.ExecuteCalls);
        Assert.Equal(0, gen.GenerateCalls);
        Assert.Equal(0, gen.AdjustCalls);
    }

    [Theory]
    [InlineData("run")]
    [InlineData(" RUN ")]
    [InlineData("cLeAr")]
    [InlineData(" Exit ")]
    public async Task ApplyInputLine_ReservedCommandParsing_IsCaseInsensitiveAndTrimmed(string input)
    {
        var gen = new FakeCommandGenerator();
        var exec = new FakeCommandExecutor();
        var session = new global::em.CommandSession(gen, exec, new global::em.CommandState());

        global::em.CommandSessionResult result = await session.ApplyInputLine(input);

        if (input.Trim().Equals("exit", System.StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(result.ShutdownRequested);
            Assert.False(result.StateChanged);
            Assert.Equal("Shutdown requested.", result.Message);
        }
        else if (input.Trim().Equals("clear", System.StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(result.ShutdownRequested);
            Assert.False(result.StateChanged); // empty state => hadAnything == false
            Assert.Equal("Cleared", result.Message);
        }
        else
        {
            // "run" with empty state
            Assert.False(result.ShutdownRequested);
            Assert.False(result.StateChanged);
            Assert.Equal("No command to run", result.Message);
        }

        Assert.Equal(0, gen.GenerateCalls);
        Assert.Equal(0, gen.AdjustCalls);
        Assert.Equal(0, exec.ExecuteCalls);
    }
}
