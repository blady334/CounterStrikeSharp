using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Tests;

public unsafe class ScriptContextSafetyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("zażółć 🦊")]
    [InlineData(null)]
    public void StringResultUsesResultSlotAndDoesNotOverwriteTrailingMemory(string? text)
    {
        var memory = (byte*)NativeMemory.AllocZeroed((nuint)sizeof(fxScriptContext) + 16);
        var context = new ScriptContext();
        try
        {
            new Span<byte>(memory + sizeof(fxScriptContext), 16).Fill(0xA5);
            var raw = (fxScriptContext*)memory;
            context.SetResult(text!, raw);
            Assert.Equal(Enumerable.Repeat((byte)0xA5, 16), new Span<byte>(memory + sizeof(fxScriptContext), 16).ToArray());
            var ptr = *(IntPtr*)raw->result;
            Assert.Equal(text, ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr));
        }
        finally { context.GlobalCleanUp(); NativeMemory.Free(memory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThirtySecondArgumentFitsButThirtyThirdCannotOverwriteResult(bool primitive)
    {
        var context = new ScriptContext();
        for (int i = 0; i < 32; i++)
        {
            if (primitive) context.PushPrimitive(i);
            else context.Push(i);
        }
        Assert.Equal(31, context.GetArgument<int>(31));
        fixed (fxScriptContext* raw = &context.m_extContext) context.SetResult(777, raw);
        if (primitive) Assert.Throws<InvalidOperationException>(() => context.PushPrimitive(123));
        else Assert.Throws<InvalidOperationException>(() => context.Push(123));
        Assert.Equal(777, context.GetResult<int>());
        context.Reset();
        context.PushPrimitive(9);
        Assert.Equal(9, context.GetArgument<int>(0));
    }

    [Fact]
    public void StringOverflowDoesNotAllocateOrModifyResult()
    {
        var context = new ScriptContext();
        try
        {
            for (int i = 0; i < 32; i++) context.Push(i);
            var before = context.GetBytes();
            Assert.Throws<InvalidOperationException>(() => context.Push("overflow"));
            Assert.Equal(before, context.GetBytes());
        }
        finally { context.GlobalCleanUp(); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(32)]
    public void ReadingOutsideSuppliedArgumentsThrows(int index)
    {
        var context = new ScriptContext();
        context.Push(42);
        Assert.Throws<ArgumentOutOfRangeException>(() => context.GetArgument<int>(index));
        Assert.Equal(42, context.GetArgument<int>(0));
    }

    private struct Wide { public long First; public long Second; }

    [Fact]
    public void OversizedPrimitiveResultCannotReadBeyondTheBuffer()
    {
        Assert.Throws<NotSupportedException>(() => new ScriptContext().GetResultPrimitive<Wide>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedArgumentDoesNotConsumeASlot(bool primitive)
    {
        var context = new ScriptContext();
        var value = new Wide { First = 123, Second = 456 };
        if (primitive) Assert.Throws<NotSupportedException>(() => context.PushPrimitive(value));
        else Assert.Throws<NotSupportedException>(() => context.Push(value));
        context.Push(7);
        Assert.Equal(7, context.GetArgument<int>(0));
    }

    [Fact]
    public void OversizedResultDoesNotModifyExistingResult()
    {
        var memory = NativeMemory.AllocZeroed((nuint)sizeof(fxScriptContext) + 16);
        var context = new ScriptContext();
        try
        {
            var raw = (fxScriptContext*)memory;
            context.SetResult(123, raw);
            Assert.Throws<NotSupportedException>(() => context.SetResult(new Wide { First = 5, Second = 6 }, (fxScriptContext*)memory));
            Assert.Equal(123, *(int*)raw->result);
        }
        finally { NativeMemory.Free(memory); }
    }
}
