using GoalDrivenAI.Characters;

namespace GoalDrivenAI.Tests;

public class CharacterTests
{
    [Test]
    public void FireObeysCadencePoliciesAndAuthoritativeAmmo()
    {
        var state = new WeaponState(3, 3, 6, 0.1, 1);
        Assert.That(state.TrySpendShot(0, false, true), Is.False);
        Assert.That(state.TrySpendShot(0, true, false), Is.False);
        Assert.That(state.Magazine, Is.EqualTo(3));
        Assert.That(state.TrySpendShot(0, true, true), Is.True);
        Assert.That(state.TrySpendShot(0.05, true, true), Is.False);
        Assert.That(state.TrySpendShot(0.1, true, true), Is.True);
        Assert.That(state.Magazine, Is.EqualTo(1));
    }

    [Test]
    public void CancelCannotRefundCommittedShotsOrTransferredAmmunition()
    {
        var state = new WeaponState(3, 1, 4, 0.1, 1);
        Assert.That(state.TrySpendShot(0, true, true), Is.True);
        Assert.That(state.BeginReload(0.1), Is.True);
        state.CancelReload();
        Assert.That(state.Tick(3), Is.False);
        Assert.That(state.Magazine, Is.Zero);
        Assert.That(state.Reserve, Is.EqualTo(4));
        Assert.That(state.BeginReload(3), Is.True);
        Assert.That(state.Tick(3.9), Is.False);
        Assert.That(state.Tick(4), Is.True);
        state.CancelReload();
        Assert.That(state.Magazine, Is.EqualTo(3));
        Assert.That(state.Reserve, Is.EqualTo(1));
    }

    [Test]
    public void InvalidNumbersCannotBypassCooldownOrCompleteReload()
    {
        var state = new WeaponState(3, 1, 4, 0.1, 1);
        Assert.That(state.TrySpendShot(double.NaN, true, true), Is.False);
        Assert.That(state.BeginReload(double.PositiveInfinity), Is.False);
        Assert.That(state.BeginReload(0), Is.True);
        Assert.That(state.Tick(double.PositiveInfinity), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeaponState(3, 1, 4, double.NaN, 1));
    }
}
