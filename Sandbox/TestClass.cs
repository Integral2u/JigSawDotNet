using JigSawDotNet;

namespace Sandbox
{
    public enum HashMethod { A, B, C, D, E, F }
    public abstract class TestClass
    {
        private int Size { get; init; }
        private byte[] Data { get; init; }
        private HashMethod Method { get; init; }
        private Func<int> GetHashDelegate { get; init; }
        public TestClass(int size, HashMethod method)
        {
            Size = size;
            Data = new byte[Size];
            Random.Shared.NextBytes(Data);
            Method = method;
            GetHashDelegate = Method == HashMethod.A ? GetHashMethodA : GetHashMethodB;
        }
        public int GetHashViaDelegate() => GetHashDelegate();
        public int GetHashViaSwitch() => Method switch
        {
            HashMethod.B => GetHashMethodB(),
            HashMethod.C => GetHashMethodA(),
            HashMethod.D => GetHashMethodA(),
            HashMethod.E => GetHashMethodA(),
            HashMethod.F => GetHashMethodA(),
            //Intnetionally put at end to consider worse case.
            //Few options can actually result in better performance
            HashMethod.A => GetHashMethodA(),
            _ => 0,
        };
        [PuzzlePlace(nameof(GetHash))]
        public abstract int GetHash();
        [PuzzlePeice(nameof(GetHash), "HashingMethod", "MethodA")]
        public int GetHashMethodA()
        {
            var result = 0;
            foreach (byte v in Data)
            {
                var t = (result * 31);
                t += v;
                result = t;
            }
            return result;
        }
        [PuzzlePeice(nameof(GetHash), "HashingMethod", "MethodB")]
        public int GetHashMethodB()
        {
            Span<byte> dataSpan = new(Data);
            var result = 0;
            for (var i = 0; i < dataSpan.Length; i++) result = (result * 31) + Data[i];
            return result;
        }
    }
}
