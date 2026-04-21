using System.Reflection;
using JigSawDotNet;
using Xunit;

namespace JigSawDotNet.Tests
{
    // -------------------------------------------------------------------------
    // Test subjects — self-contained abstract classes defined here so tests
    // have no dependency on the Sandbox project.
    // -------------------------------------------------------------------------

    // Standard two-piece subject
    public abstract class Calculator
    {
        protected readonly int Value;
        public Calculator(int value) => Value = value;

        [PuzzlePlace(nameof(Compute))]
        public abstract int Compute();

        [PuzzlePeice(nameof(Compute), "Mode", "Double")]
        public int ComputeDouble() => Value * 2;

        [PuzzlePeice(nameof(Compute), "Mode", "Triple")]
        public int ComputeTriple() => Value * 3;
    }

    // Subject with a parameterless constructor
    public abstract class ParameterlessCalculator
    {
        [PuzzlePlace(nameof(Compute))]
        public abstract int Compute();

        [PuzzlePeice(nameof(Compute), "Mode", "Forty")]
        public int ComputeForty() => 40;
    }

    // Subject with multiple puzzle places
    public abstract class MultiPlace
    {
        protected readonly int Value;
        public MultiPlace(int value) => Value = value;

        [PuzzlePlace(nameof(Add))]
        public abstract int Add();

        [PuzzlePlace(nameof(Multiply))]
        public abstract int Multiply();

        [PuzzlePeice(nameof(Add), "AddMode", "PlusOne")]
        public int AddOne() => Value + 1;

        [PuzzlePeice(nameof(Add), "AddMode", "PlusTen")]
        public int AddTen() => Value + 10;

        [PuzzlePeice(nameof(Multiply), "MulMode", "ByTwo")]
        public int MultiplyByTwo() => Value * 2;

        [PuzzlePeice(nameof(Multiply), "MulMode", "ByFive")]
        public int MultiplyByFive() => Value * 5;
    }

    // Subject with a private field — tests IL copy access grants
    public abstract class PrivateFieldSubject
    {
        private readonly int _secret;
        public PrivateFieldSubject(int secret) => _secret = secret;

        [PuzzlePlace(nameof(Reveal))]
        public abstract int Reveal();

        [PuzzlePeice(nameof(Reveal), "Mode", "Direct")]
        public int RevealDirect() => _secret;

        [PuzzlePeice(nameof(Reveal), "Mode", "Doubled")]
        public int RevealDoubled() => _secret * 2;
    }

    // Subject whose [PuzzlePlace] is NOT abstract — should throw
    public abstract class NonAbstractPlace
    {
        [PuzzlePlace(nameof(Compute))]
        public int Compute() => 0;   // not abstract

        [PuzzlePeice(nameof(Compute), "Mode", "A")]
        public int ComputeA() => 1;
    }

    // Concrete class — Assemble<T> should throw
    public class ConcreteClass
    {
        public int Value => 0;
    }

    // Subject with no [PuzzlePlace] at all
    public abstract class NoPuzzlePlaces
    {
        [PuzzlePeice(nameof(Orphan), "Mode", "A")]
        public int Orphan() => 1;
    }

    // Subject with a method that takes parameters
    public abstract class WithParameters
    {
        [PuzzlePlace(nameof(Transform))]
        public abstract int Transform(int x, int y);

        [PuzzlePeice(nameof(Transform), "Op", "Add")]
        public int TransformAdd(int x, int y) => x + y;

        [PuzzlePeice(nameof(Transform), "Op", "Multiply")]
        public int TransformMultiply(int x, int y) => x * y;
    }
    public static class ExternalMath
    {
        /// <summary>Multiply a + b — used by FQN resolution test.</summary>
        public static int Add(int a, int b) => a + b;

        /// <summary>Subtract a - b — used by RegisterCorner test.</summary>
        public static int Subtract(int a, int b) => a - b;

    }
    public abstract class MathOps
    {
        [PuzzleCornerPiece("Add", "AddExternal", "JigSawDotNet.Tests.ExternalMath.Add", "AddInternal", "AddInternal")]
        public abstract int Add(int a, int b);
        public static int AddInternal(int a, int b) => a + b;
    }
    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    public class AssemblerTests
    {
        public AssemblerTests()
        {
            Assembler.Cache = false;
        }
        public void Dispose() => Assembler.Cache = true; // restore default
        // -----------------------------------------------------------------
        // Assemble<T>
        // -----------------------------------------------------------------

        [Fact]
        public void CreateInstance_CornerPeice_Local()
        {
            var ops = Assembler.CreateInstance<MathOps>(
                new Dictionary<string, string> { ["Add"] = "AddInternal" });

            Assert.Equal(5, ops.Add(2, 3));
        }
        [Fact]
        public void CreateInstance_CornerPeice_External()
        {
            var ops = Assembler.CreateInstance<MathOps>(
                new Dictionary<string, string> { ["Add"] = "AddExternal" });

            Assert.Equal(5, ops.Add(2, 3));
        }
        [Fact]
        public void Assemble_ThrowsForConcreteType()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => Assembler.Assemble<ConcreteClass>([]));
            Assert.Contains("must be abstract", ex.Message);
        }

        [Fact]
        public void Assemble_ThrowsForNullMapping()
        {
            Assert.Throws<ArgumentNullException>(
                () => Assembler.Assemble<Calculator>(null!));
        }

        [Fact]
        public void Assemble_ReturnsOriginalTypeWhenNoPuzzlePlaces()
        {
            var result = Assembler.Assemble<NoPuzzlePlaces>([]);
            Assert.Equal(typeof(NoPuzzlePlaces), result);
        }

        [Fact]
        public void Assemble_ThrowsWhenNoPieceMatchesMapping()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => Assembler.Assemble<Calculator>(new Dictionary<string, string>
                {
                    ["Mode"] = "NonExistent"
                }));
            Assert.Contains("no valid", ex.Message);
        }

        [Fact]
        public void Assemble_ThrowsWhenPlaceIsNotAbstract()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => Assembler.Assemble<NonAbstractPlace>(new Dictionary<string, string>
                {
                    ["Mode"] = "A"
                }));
            Assert.Contains("must be abstract", ex.Message);
        }

        [Fact]
        public void Assemble_ReturnsSealedConcreteSubtype()
        {
            var type = Assembler.Assemble<Calculator>(new Dictionary<string, string>
            {
                ["Mode"] = "Double"
            });
            Assert.True(type.IsSealed);
            Assert.True(typeof(Calculator).IsAssignableFrom(type));
        }

        [Fact]
        public void Assemble_ReturnsCachedTypeOnSecondCall()
        {
            Assembler.Cache = true;
            var mapping = new Dictionary<string, string> { ["Mode"] = "Double" };
            var first = Assembler.Assemble<Calculator>(mapping);
            var second = Assembler.Assemble<Calculator>(mapping);
            Assert.Same(first, second);
            Assembler.Cache = false;
        }

        [Fact]
        public void Assemble_ReturnsDifferentTypesForDifferentMappings()
        {
            var typeA = Assembler.Assemble<Calculator>(new Dictionary<string, string> { ["Mode"] = "Double" });
            var typeB = Assembler.Assemble<Calculator>(new Dictionary<string, string> { ["Mode"] = "Triple" });
            Assert.NotSame(typeA, typeB);
        }

        // -----------------------------------------------------------------
        // CreateInstance<T> — correct implementation is called
        // -----------------------------------------------------------------

        [Fact]
        public void CreateInstance_CallsCorrectPiece_Double()
        {
            var instance = Assembler.CreateInstance<Calculator>(
                new Dictionary<string, string> { ["Mode"] = "Double" }, 5);
            Assert.Equal(10, instance.Compute());
        }

        [Fact]
        public void CreateInstance_CallsCorrectPiece_Triple()
        {
            var instance = Assembler.CreateInstance<Calculator>(
                new Dictionary<string, string> { ["Mode"] = "Triple" }, 5);
            Assert.Equal(15, instance.Compute());
        }

        [Fact]
        public void CreateInstance_WorksWithParameterlessConstructor()
        {
            var instance = Assembler.CreateInstance<ParameterlessCalculator>(
                new Dictionary<string, string> { ["Mode"] = "Forty" });
            Assert.Equal(40, instance.Compute());
        }

        [Fact]
        public void CreateInstance_WorksWithMethodParameters()
        {
            var add = Assembler.CreateInstance<WithParameters>(
                new Dictionary<string, string> { ["Op"] = "Add" });
            var mul = Assembler.CreateInstance<WithParameters>(
                new Dictionary<string, string> { ["Op"] = "Multiply" });

            Assert.Equal(7, add.Transform(3, 4));
            Assert.Equal(12, mul.Transform(3, 4));
        }

        [Fact]
        public void CreateInstance_AccessesPrivateFields()
        {
            // Verifies IgnoresAccessChecksTo is working — IL copy can reach private members
            var direct = Assembler.CreateInstance<PrivateFieldSubject>(
                new Dictionary<string, string> { ["Mode"] = "Direct" }, 99);
            var doubled = Assembler.CreateInstance<PrivateFieldSubject>(
                new Dictionary<string, string> { ["Mode"] = "Doubled" }, 99);

            Assert.Equal(99, direct.Reveal());
            Assert.Equal(198, doubled.Reveal());
        }

        [Fact]
        public void CreateInstance_AllPlacesFilledForMultiPlaceType()
        {
            var instance = Assembler.CreateInstance<MultiPlace>(
                new Dictionary<string, string>
                {
                    ["AddMode"] = "PlusTen",
                    ["MulMode"] = "ByFive"
                }, 3);

            Assert.Equal(13, instance.Add());
            Assert.Equal(15, instance.Multiply());
        }

        [Fact]
        public void CreateInstance_DifferentPiecesProduceDifferentResults()
        {
            var plusOne = Assembler.CreateInstance<MultiPlace>(
                new Dictionary<string, string>
                {
                    ["AddMode"] = "PlusOne",
                    ["MulMode"] = "ByTwo"
                }, 4);

            var plusTen = Assembler.CreateInstance<MultiPlace>(
                new Dictionary<string, string>
                {
                    ["AddMode"] = "PlusTen",
                    ["MulMode"] = "ByTwo"
                }, 4);

            Assert.Equal(5, plusOne.Add());
            Assert.Equal(14, plusTen.Add());
            // Multiply piece is the same in both
            Assert.Equal(8, plusOne.Multiply());
            Assert.Equal(8, plusTen.Multiply());
        }

        [Fact]
        public void CreateInstance_ProducesConsistentResultsAcrossMultipleCalls()
        {
            var mapping = new Dictionary<string, string> { ["Mode"] = "Double" };
            var a = Assembler.CreateInstance<Calculator>(mapping, 7);
            var b = Assembler.CreateInstance<Calculator>(mapping, 7);
            Assert.Equal(a.Compute(), b.Compute());
        }

        // -----------------------------------------------------------------
        // CreateInstanceForSystem<T>
        // -----------------------------------------------------------------

        [Fact]
        public void CreateInstanceForSystem_ReturnsInstance()
        {
            var instance = Assembler.CreateInstanceForSystem<Calculator>(
                getArgsFor: _ => [],
                bestCombination: out _,
                constructorArgs: [10]);

            Assert.NotNull(instance);
            Assert.IsAssignableFrom<Calculator>(instance);
        }

        [Fact]
        public void CreateInstanceForSystem_ReturnsCorrectResult()
        {
            // Either Double (20) or Triple (30) — both are valid outcomes
            var instance = Assembler.CreateInstanceForSystem<Calculator>(
                getArgsFor: _ => [],
                bestCombination: out _,
                constructorArgs: [10]);

            var result = instance.Compute();
            Assert.True(result == 20 || result == 30,
                $"Expected 20 or 30, got {result}");
        }

        [Fact]
        public void CreateInstanceForSystem_PopulatesBestCombination()
        {
            Assembler.CreateInstanceForSystem<Calculator>(
                getArgsFor: _ => [],
                bestCombination: out var best,
                constructorArgs: [10]);

            Assert.NotEmpty(best);
            Assert.True(best.ContainsKey("Mode"));
            Assert.Contains(best["Mode"], new HashSet<string>(["Double", "Triple"]));
        }

        [Fact]
        public void CreateInstanceForSystem_RespectsFullyPinnedMapping()
        {
            var instance = Assembler.CreateInstanceForSystem<Calculator>(
                getArgsFor: _ => [],
                mapping: new Dictionary<string, string> { ["Mode"] = "Double" },
                bestCombination: out var best,
                constructorArgs: [10]);

            // Only one combination was possible — must pick Double
            Assert.Equal("Double", best["Mode"]);
            Assert.Equal(20, instance.Compute());
        }

        [Fact]
        public void CreateInstanceForSystem_ThrowsWhenNoPuzzlePlaces()
        {
            Assert.Throws<InvalidOperationException>(
                () => Assembler.CreateInstanceForSystem<NoPuzzlePlaces>(
                    getArgsFor: _ => [],
                    bestCombination: out _));
        }

        // -----------------------------------------------------------------
        // GetJigSawPuzzle<T>
        // -----------------------------------------------------------------

        [Fact]
        public void GetJigSawPuzzle_ReturnsAllPlaces()
        {
            var puzzle = Assembler.GetJigSawPuzzle<Calculator>();
            Assert.Single(puzzle);
            Assert.Equal("Compute", puzzle.Keys.Single().Name);
        }

        [Fact]
        public void GetJigSawPuzzle_ReturnsAllPiecesForPlace()
        {
            var puzzle = Assembler.GetJigSawPuzzle<Calculator>();
            var pieces = puzzle.Values.Single();
            Assert.Equal(2, pieces.Count);

            var values = pieces
                .Select(p => p.GetCustomAttribute<PuzzlePeice>()!.Value)
                .ToHashSet();

            Assert.Contains("Double", values);
            Assert.Contains("Triple", values);
        }

        [Fact]
        public void GetJigSawPuzzle_ReturnsMultiplePlaces()
        {
            var puzzle = Assembler.GetJigSawPuzzle<MultiPlace>();
            Assert.Equal(2, puzzle.Count);

            var placeNames = puzzle.Keys.Select(k => k.Name).ToHashSet();
            Assert.Contains("Add", placeNames);
            Assert.Contains("Multiply", placeNames);
        }

        [Fact]
        public void GetJigSawPuzzle_ReturnsEmptyForNoPuzzlePlaces()
        {
            var puzzle = Assembler.GetJigSawPuzzle<NoPuzzlePlaces>();
            Assert.Empty(puzzle);
        }
    }
}
