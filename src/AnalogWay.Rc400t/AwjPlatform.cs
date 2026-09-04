namespace AnalogWay.Rc400t
{
    /// <summary>
    /// AWJ ("Analog Way JSON") device families reachable through an RC400T event controller.
    /// The RC400T itself is a hardware control surface, not a network-addressable target: it drives the
    /// same processors over the same protocol this module speaks. Selecting the right platform here
    /// makes this module behave like a second RC400T-equivalent client on the network.
    /// </summary>
    public enum AwjPlatform
    {
        /// <summary>Aquilon C / Alta 4K running LivePremier firmware 4.x and later ("screenAuxGroupList" model).</summary>
        LivePremier4 = 0,

        /// <summary>Midra 4K family: Eikos, Pulse, QuickMatrix, QuickVu, Zenith 100/200.</summary>
        Midra = 1
    }

    /// <summary>Which of the two transition buses (A/B, i.e. Program/Preview) an operation targets.</summary>
    public enum AwjPresetBus
    {
        A = 0,
        B = 1
    }
}
