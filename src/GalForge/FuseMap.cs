using System;

namespace GalForge;

/// <summary>
/// A device fuse array under construction. Each family has its own region
/// shape; the one thing the writer needs is the flattened, address-ordered
/// vector, so that is the base contract.
///
/// Convention (all families): a value of 1 emits '1' in the JEDEC list, 0
/// emits '0'. The default (unlisted) state is 0, declared by the writer's
/// *F0 field. The meaning of 1 vs 0 is the device's; the mapper sets these
/// to match it.
/// </summary>
internal abstract class FuseMapBase
{
    public readonly TargetDevice Device;

    protected FuseMapBase(TargetDevice device) => Device = device;

    /// <summary>Every fuse flattened into one vector in JEDEC address order.
    /// Length == Device.FuseCount.</summary>
    public abstract byte[] ToLinear();
}

/// <summary>
/// The 16V8/20V8 fuse map, partitioned into the GAL regions in JEDEC
/// address order: the AND array plus XOR (polarity), signature, AC1,
/// product-term-disable (PT), and the SYN / AC0 architecture bits.
/// </summary>
internal sealed class FuseMap : FuseMapBase
{
    public readonly byte[] Logic;   // AND array, row-major: row r at [r*Columns ..]
    public readonly byte[] Xor;     // output polarity, one per OLMC
    public readonly byte[] Sig;     // user signature
    public readonly byte[] Ac1;     // AC1 bits
    public readonly byte[] Pt;      // product-term disable
    public byte Syn;                // SYN architecture bit
    public byte Ac0;                // AC0 architecture bit

    public FuseMap(GalV8Device device) : base(device)
    {
        Logic = new byte[device.LogicSize];
        Xor = new byte[GalV8Device.XorSize];
        Sig = new byte[GalV8Device.SigSize];
        Ac1 = new byte[GalV8Device.Ac1Size];
        Pt = new byte[GalV8Device.PtSize];
    }

    /// <summary>Address order: Logic, XOR, Sig, AC1, PT, SYN, AC0.</summary>
    public override byte[] ToLinear()
    {
        var all = new byte[Device.FuseCount];
        int at = 0;
        Array.Copy(Logic, 0, all, at, Logic.Length); at += Logic.Length;
        Array.Copy(Xor, 0, all, at, Xor.Length); at += Xor.Length;
        Array.Copy(Sig, 0, all, at, Sig.Length); at += Sig.Length;
        Array.Copy(Ac1, 0, all, at, Ac1.Length); at += Ac1.Length;
        Array.Copy(Pt, 0, all, at, Pt.Length); at += Pt.Length;
        all[at++] = Syn;
        all[at++] = Ac0;
        return all;
    }
}

/// <summary>
/// The 22V10 fuse map: the 132x44 AND array (which contains the AR row, the
/// ten OLMC blocks with their OE rows, and the SP row -- see
/// <see cref="Gal22V10"/>), the twenty S0/S1 macrocell-configuration bits,
/// and the UES. No XOR/AC1/PT/SYN/AC0 regions exist on this family.
/// </summary>
internal sealed class FuseMap22V10 : FuseMapBase
{
    public readonly byte[] Logic = new byte[Gal22V10.LogicSize];
    public readonly byte[] SBits = new byte[Gal22V10.SBitsSize];
    public readonly byte[] Ues = new byte[Gal22V10.UesSize];

    public FuseMap22V10(Gal22V10 device) : base(device) { }

    /// <summary>Address order: Logic (0..5807), S bits (5808..5827), UES (5828..5891).</summary>
    public override byte[] ToLinear()
    {
        var all = new byte[Device.FuseCount];
        int at = 0;
        Array.Copy(Logic, 0, all, at, Logic.Length); at += Logic.Length;
        Array.Copy(SBits, 0, all, at, SBits.Length); at += SBits.Length;
        Array.Copy(Ues, 0, all, at, Ues.Length);
        return all;
    }
}
