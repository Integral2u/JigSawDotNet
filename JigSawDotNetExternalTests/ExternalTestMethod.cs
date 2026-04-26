using JigSawDotNet;

namespace JigSawDotNetExternalTests
{
    public static class ExternalTestMethod
    {
        [PuzzlePeice("BoolOperation","InternalExternal", "External")]
        public static bool Not(bool value) => !value;
    }
}
