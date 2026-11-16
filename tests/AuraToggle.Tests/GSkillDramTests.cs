using System;
using Xunit;

namespace AuraToggle.Tests;

/// <summary>
/// The ENE register encoding for G.Skill DRAM. No bus and no driver: these only check that a
/// look becomes the bytes the controller expects, and that a flash commit cannot be sent.
/// </summary>
public sealed class GSkillDramTests
{
    [Fact]
    public void RegisterAddressIsByteSwappedOntoTheWire()
    {
        Assert.Equal(0x2180, EneProtocol.WireWord(0x8021));
        Assert.Equal(0xA080, EneProtocol.WireWord(0x80A0));
        Assert.Equal(0x0010, EneProtocol.WireWord(0x1000));
    }

    [Fact]
    public void ColoursAreWrittenRedBlueGreen()
    {
        byte[] buffer = EneProtocol.ColorBuffer(2, 0x11, 0x22, 0x33);

        Assert.Equal(new byte[] { 0x11, 0x33, 0x22, 0x11, 0x33, 0x22 }, buffer);
    }

    [Fact]
    public void LedCountComesFromTheConfigTableAndFallsBackToEight()
    {
        Assert.Equal(8, EneProtocol.LedCount(new byte[] { 0, 0, 8, 0 }));
        Assert.Equal(10, EneProtocol.LedCount(new byte[] { 0, 0, 8, 10 }));
        Assert.Equal(8, EneProtocol.LedCount(new byte[] { 0, 0, 0, 0 }));
        Assert.Equal(8, EneProtocol.LedCount(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void EffectRegisterFollowsTheFirmwareFamily()
    {
        Assert.Equal(new ushort[] { 0x8160 }, EneProtocol.EffectRegisters("AUMA0-E6K5-0101"));
        Assert.Equal(new ushort[] { 0x8010 }, EneProtocol.EffectRegisters("DIMM_LED-0103"));
        Assert.Equal(new ushort[] { 0x8010 }, EneProtocol.EffectRegisters("AUMA0-E8K4-0101"));
        Assert.Equal(new ushort[] { 0x8010, 0x8160 }, EneProtocol.EffectRegisters(""));
    }

    [Fact]
    public void FlashCommitAndUnknownRegistersAreRefused()
    {
        EneProtocol.EnsureWrite(0x80A0, 0x01);
        EneProtocol.EnsureWrite(0x8021, 0x00);
        EneProtocol.EnsureWrite(0x8010, 0xFF);

        Assert.Throws<InvalidOperationException>(() => EneProtocol.EnsureWrite(0x80A0, 0xAA));
        Assert.Throws<InvalidOperationException>(() => EneProtocol.EnsureWrite(0x80F9, 0x50 << 1));
        Assert.Throws<InvalidOperationException>(() => EneProtocol.EnsureWrite(0x1000, 0x00));
    }

    [Fact]
    public void SlotsAreNamedLikeTheBoardSilkscreen()
    {
        Assert.Equal("DIMM 1", EneProtocol.SlotLabel(0));
        Assert.Equal("DIMM 2", EneProtocol.SlotLabel(1));
        Assert.Equal("DIMM 8", EneProtocol.SlotLabel(7));
    }
}
