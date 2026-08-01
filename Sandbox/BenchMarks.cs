/*
| Method             | N    | Mean       | Error    | StdDev    | Median     |
|------------------- |----- |-----------:|---------:|----------:|-----------:|
| MethodAViaDelegate | 1000 | 1,349.4 ns | 16.72 ns |  15.64 ns | 1,347.7 ns |
| MethodAViaSwitch   | 1000 | 1,064.2 ns | 48.64 ns | 142.66 ns |   993.8 ns |
| MethodAJigSaw      | 1000 |   990.8 ns | 17.54 ns |  27.81 ns |   989.9 ns |
| MethodADirect      | 1000 | 1,152.4 ns | 63.49 ns | 187.20 ns | 1,025.4 ns |
| MethodBDirect      | 1000 | 1,347.8 ns | 23.62 ns |  20.94 ns | 1,345.2 ns |
| MethodBJigSaw      | 1000 | 1,150.4 ns | 59.78 ns | 176.25 ns | 1,127.9 ns |
| MethodCJigSaw      | 1000 |   988.9 ns | 19.53 ns |  22.49 ns |   985.4 ns |
*/
using BenchmarkDotNet.Attributes;
using System.Reflection;

namespace Sandbox
{
    //[MemoryDiagnoser]
    public class BenchMarks
    {
        [Params(1000)]
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
