/*
| Method             | N    | Mean     | Error     | StdDev    | Median   |
|------------------- |----- |---------:|----------:|----------:|---------:|
| MethodAViaDelegate | 1000 | 1.348 us | 0.0159 us | 0.0141 us | 1.351 us |
| MethodAViaSwitch   | 1000 | 1.223 us | 0.0524 us | 0.1546 us | 1.300 us |
| MethodAJigSaw      | 1000 | 1.249 us | 0.0480 us | 0.1376 us | 1.309 us |
| MethodADirect      | 1000 | 1.229 us | 0.0516 us | 0.1522 us | 1.309 us |
| MethodBDirect      | 1000 | 1.623 us | 0.0554 us | 0.1626 us | 1.696 us |
| MethodBJigSaw      | 1000 | 1.380 us | 0.0271 us | 0.0371 us | 1.379 us |
| MethodCJigSaw      | 1000 | 1.307 us | 0.0209 us | 0.0186 us | 1.308 us |
*/
using BenchmarkDotNet.Attributes;
using System.Reflection;

namespace Sandbox
{
    //[MemoryDiagnoser]
    public class BenchMarks
    {
        [Params(5000)]
        public int N;
        public TestClass TestClassA;
        public TestClass TestClassB;
        public TestClass TestClassC;
        [GlobalSetup]
        public void Setup()
        {
            // var typeA = JigSawDotNet.Assembler.Assemble<TestClass>(new Dictionary<string, string>
            // {
            //    ["HashingMethod"] = "MethodA"
            // });
            TestClassA = JigSawDotNet.Assembler.CreateInstance<TestClass>(new Dictionary<string, string>
            {
                ["HashingMethod"] = "MethodA"
            }, N, HashMethod.A);
            TestClassB = JigSawDotNet.Assembler.CreateInstance<TestClass>(new Dictionary<string, string>
            {
                ["HashingMethod"] = "MethodB"
            }, N, HashMethod.B);
            TestClassC = JigSawDotNet.Assembler.CreateInstanceForSystem<TestClass>(GetArgsFor, out _, N, HashMethod.A);

        }

        private object?[]? GetArgsFor(MethodInfo info)
        {
            if (info.Name == "GetHash") return [];
            throw new MissingMethodException();
        }

        [Benchmark]
        public int MethodAViaDelegate() => TestClassA.GetHashViaDelegate();
        [Benchmark]
        public int MethodAViaSwitch() => TestClassA.GetHashViaSwitch();
        [Benchmark]
        public int MethodAJigSaw() => TestClassA.GetHash();
        [Benchmark]
        public int MethodADirect() => TestClassA.GetHashMethodA();
        [Benchmark]
        public int MethodBDirect() => TestClassB.GetHashMethodB();
        [Benchmark]
        public int MethodBJigSaw() => TestClassB.GetHash();
        [Benchmark]
        public int MethodCJigSaw() => TestClassC.GetHash();
    }
}
