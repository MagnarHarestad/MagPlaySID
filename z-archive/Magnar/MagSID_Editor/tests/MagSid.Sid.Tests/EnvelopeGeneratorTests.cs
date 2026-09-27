using MagSid.Sid;

namespace MagSid.Sid.Tests;

/// <summary>Behavioral tests that indirectly verify the ported rate_counter_period and
/// sustain_level tables are wired correctly (rather than reaching into the private tables
/// directly - these exercise the same public API a real player would use).</summary>
public class EnvelopeGeneratorTests
{
    [Fact]
    public void Attack_rate_0_steps_the_envelope_every_9_cycles()
    {
        // rate_counter_period[0] = 9 in the source.
        var env = new EnvelopeGenerator();
        env.WriteAttackDecay(0x00); // attack=0, decay=0
        env.WriteSustainRelease(0x00);
        env.WriteControlReg(0x01); // gate on

        for (int i = 0; i < 8; i++)
        {
            env.Clock();
        }
        Assert.Equal(0, env.Output()); // not yet stepped

        env.Clock(); // 9th cycle
        Assert.Equal(1, env.Output());

        for (int i = 0; i < 9; i++)
        {
            env.Clock();
        }
        Assert.Equal(2, env.Output());
    }

    [Fact]
    public void Envelope_reaches_0xff_at_the_end_of_attack_then_holds_at_sustain_level()
    {
        // sustain_level[8] = 0x88 in the source.
        var env = new EnvelopeGenerator();
        env.WriteAttackDecay(0x00); // attack=0 (9 cycles/step), decay=0 (9 cycles/step)
        env.WriteSustainRelease(0x80); // sustain=8, release=0
        env.WriteControlReg(0x01); // gate on

        // 255 steps to reach 0xff, 9 cycles each.
        for (int i = 0; i < 255 * 9; i++)
        {
            env.Clock();
        }
        Assert.Equal(0xff, env.Output());

        // Decay from 0xff down to the sustain level 0x88 = 0xff - 0x88 = 0x77 = 119 steps.
        // Decay uses the exponential counter, so this takes considerably more than
        // 119*9 cycles once below envelope value 0x5d - clock generously past that point
        // and confirm it settles exactly at the sustain level and stays there.
        for (int i = 0; i < 20000; i++)
        {
            env.Clock();
        }
        Assert.Equal(0x88, env.Output());

        for (int i = 0; i < 1000; i++)
        {
            env.Clock();
        }
        Assert.Equal(0x88, env.Output()); // holds, doesn't decay further
    }

    [Fact]
    public void Gate_off_starts_release_from_the_current_level()
    {
        var env = new EnvelopeGenerator();
        env.WriteAttackDecay(0x00);
        env.WriteSustainRelease(0xf0); // sustain=15 (=0xff, i.e. no decay), release=0
        env.WriteControlReg(0x01); // gate on

        for (int i = 0; i < 255 * 9; i++)
        {
            env.Clock();
        }
        Assert.Equal(0xff, env.Output());

        env.WriteControlReg(0x00); // gate off -> release
        for (int i = 0; i < 9; i++)
        {
            env.Clock();
        }
        Assert.Equal(0xfe, env.Output());
    }
}
