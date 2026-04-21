using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}
namespace JigSawDotNet
{
    public record MethodReference(string Key, string FullyQualifiedStaticMethod);
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public class PuzzlePlace(string pointer) : Attribute
    {
        //The abstract method to place this method peice
        public string Pointer { get; init; } = pointer;
    }
    [AttributeUsage(AttributeTargets.Method, Inherited = false)] //Expect excptions if not used correctly
    public class PuzzleCornerPiece : Attribute
    {
        public string Pointer { get; init; }
        public IDictionary<string, string> KeyValues { get; set; } = new Dictionary<string, string>(); //(FullyQualifiedName id the goto but allow lazy in class definitions
        public PuzzleCornerPiece(string pointer, params string[] keyValues)
        {
            Pointer = pointer;
            if (keyValues.Length % 2 != 0)
                throw new ArgumentException("attribute values should be set in pairs");
            for (var i = 0; i < keyValues.Length; i += 2)
            {
                var (key, value) = (keyValues[i], keyValues[i + 1]);
                KeyValues.Add(key, value);
            }
        }
    }
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public class PuzzlePeice(string pointer, string key, string value) : Attribute
    {
        //Pointer to the abstract method to place this method peice
        public string Pointer { get; init; } = pointer;
        //The condition Key to match to use this method peice
        public string Key { get; init; } = key;
        //The condition Value to match to use this method peice
        public string Value { get; init; } = value;
    }
    public static class Assembler
    {
        private static bool _cache = true;
        public static bool Cache { get => _cache; set { if (!value) AssemblableMappings.Clear(); _cache = value; } }
        private static readonly Dictionary<string, Type> AssemblableMappings = [];

        public static Type Assemble<T>(Dictionary<string, string> mapping)
        {
            IEnumerable<MethodInfo> GetPuzzlePeicesFor(MethodInfo method, string pointer, IEnumerable<MethodInfo> puzzlePeices)
            {
                foreach (var candidate in puzzlePeices)
                {
                    var puzzlePeice = candidate.GetCustomAttribute<PuzzlePeice>();
                    if (puzzlePeice == null) continue;
                    if (puzzlePeice.Pointer != pointer) continue;  // ← fixes both failures
                    if (string.IsNullOrWhiteSpace(puzzlePeice.Key)) continue;
                    if (!mapping.TryGetValue(puzzlePeice.Key, out var value)) continue;
                    if (string.IsNullOrWhiteSpace(value) || value != puzzlePeice.Value) continue;
                    if (method.DeclaringType == candidate.DeclaringType
                        && method.ReturnType == candidate.ReturnType
                        && method.GetParameters()
                                 .Select(p => p.ParameterType)
                                 .SequenceEqual(candidate.GetParameters().Select(p => p.ParameterType)))
                        yield return candidate;
                }
            }
            static void CopyCustomAttributes(MethodInfo source, MethodBuilder target)
            {
                // --- Method-level attributes ---
                foreach (var attr in source.GetCustomAttributesData())
                {
                    if (attr.AttributeType == typeof(PuzzlePeice)) continue;
                    if (attr.AttributeType == typeof(PuzzlePlace)) continue;
                    var ctorArgs = attr.ConstructorArguments
                                        .Select(a => a.Value)
                                        .ToArray();

                    var namedProps = attr.NamedArguments
                                         .Where(a => !a.IsField)
                                         .Select(a => (PropertyInfo)a.MemberInfo)
                                         .ToArray();

                    var propValues = attr.NamedArguments
                                         .Where(a => !a.IsField)
                                         .Select(a => a.TypedValue.Value)
                                         .ToArray();

                    var namedFields = attr.NamedArguments
                                         .Where(a => a.IsField)
                                         .Select(a => (FieldInfo)a.MemberInfo)
                                         .ToArray();

                    var fieldValues = attr.NamedArguments
                                         .Where(a => a.IsField)
                                         .Select(a => a.TypedValue.Value)
                                         .ToArray();

                    var cab = new CustomAttributeBuilder(
                        attr.Constructor,
                        ctorArgs,
                        namedProps, propValues,
                        namedFields, fieldValues);

                    target.SetCustomAttribute(cab);
                }

                // --- Parameter-level attributes ---
                var sourceParams = source.GetParameters();
                for (int i = 0; i < sourceParams.Length; i++)
                {
                    // i+1 because position is 1-based (0 = return value)
                    var paramBuilder = target.DefineParameter(
                        i + 1,
                        sourceParams[i].Attributes,
                        sourceParams[i].Name);

                    foreach (var attr in sourceParams[i].GetCustomAttributesData())
                    {
                        var ctorArgs = attr.ConstructorArguments.Select(a => a.Value).ToArray();
                        var namedProps = attr.NamedArguments.Where(a => !a.IsField)
                                             .Select(a => (PropertyInfo)a.MemberInfo).ToArray();
                        var propValues = attr.NamedArguments.Where(a => !a.IsField)
                                             .Select(a => a.TypedValue.Value).ToArray();
                        var namedFields = attr.NamedArguments.Where(a => a.IsField)
                                             .Select(a => (FieldInfo)a.MemberInfo).ToArray();
                        var fieldValues = attr.NamedArguments.Where(a => a.IsField)
                                             .Select(a => a.TypedValue.Value).ToArray();

                        paramBuilder.SetCustomAttribute(new CustomAttributeBuilder(
                            attr.Constructor,
                            ctorArgs,
                            namedProps, propValues,
                            namedFields, fieldValues));
                    }
                }

                // --- Return type attributes ---
                var returnBuilder = target.DefineParameter(0, ParameterAttributes.Retval, null);
                foreach (var attr in source.ReturnParameter.GetCustomAttributesData())
                {
                    var ctorArgs = attr.ConstructorArguments.Select(a => a.Value).ToArray();
                    returnBuilder.SetCustomAttribute(
                        new CustomAttributeBuilder(attr.Constructor, ctorArgs));
                }
            }
            static void DefineConstructor(TypeBuilder typeBuilder, Type baseType, Type[] constructorArgTypes)
            {
                // Get the matching constructor from the base class
                ConstructorInfo baseCtor = baseType.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    constructorArgTypes,
                    null)!;

                // Define a constructor with the same parameter types
                var ctorBuilder = typeBuilder.DefineConstructor(
                    MethodAttributes.Public,
                    CallingConventions.Standard,
                    constructorArgTypes);

                var il = ctorBuilder.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);               // load 'this'

                // Load each constructor argument
                for (int i = 0; i < constructorArgTypes.Length; i++)
                    il.Emit(OpCodes.Ldarg, i + 1);

                il.Emit(OpCodes.Call, baseCtor);        // call base(...)
                il.Emit(OpCodes.Ret);
            }
            ArgumentNullException.ThrowIfNull(mapping);
            var classType = typeof(T);

            // Validation
            if (!classType.IsAbstract) throw new InvalidOperationException($"Type {classType.FullName} must be abstract.");
            var fullName = $"{classType.FullName}{mapping.GetHashCode()}";
            if (Cache && AssemblableMappings.TryGetValue(fullName, out var assembled)) return assembled;

            var puzzlePlaces = classType.GetMethods().Where(m => m.GetCustomAttributes(typeof(PuzzlePlace), true).Length != 0);
            var puzzleCornerPlaces = classType.GetMethods().Where(m => m.GetCustomAttributes(typeof(PuzzleCornerPiece), true).Length != 0);
            if (!puzzlePlaces.Any() && !puzzleCornerPlaces.Any()) return classType;
            var puzzlePeices = classType.GetMethods().Where(m => m.GetCustomAttributes(typeof(PuzzlePeice), true).Length != 0);

            var buildList = new List<(MethodInfo Destination, MethodInfo Source)>();
            foreach (var place in puzzlePlaces)
            {
                if (!place.IsAbstract) throw new InvalidOperationException($"Method {place.Name} must be abstract to use [{nameof(PuzzlePlace)}].");
                var puzzlePlace = place.GetCustomAttribute<PuzzlePlace>();
                if (puzzlePlace == null) continue; //Never the case
                if (string.IsNullOrWhiteSpace(puzzlePlace.Pointer)) throw new InvalidOperationException($"[{nameof(PuzzlePlace)}.{nameof(PuzzlePlace.Pointer)}] must not be null or empty.");
                var peices = GetPuzzlePeicesFor(place, puzzlePlace.Pointer, puzzlePeices);
                var count = peices.Take(2).Count();
                if (count == 0) throw new InvalidOperationException($"Method {place.Name} has no valid [{nameof(PuzzlePeice)}] to use.");
                if (count != 1) throw new InvalidOperationException($"Method {place.Name} has multiple valid [{nameof(PuzzlePeice)}] to use, Must only be one viable candidate for mappings.");
                buildList.Add((place, peices.Single()));
            }
            foreach (var place in puzzleCornerPlaces)
            {
                if (!place.IsAbstract) throw new InvalidOperationException($"Method {place.Name} must be abstract to use [{nameof(PuzzlePlace)}].");
                var puzzleCornerPlace = place.GetCustomAttribute<PuzzleCornerPiece>();
                if (puzzleCornerPlace == null) continue; //Never the case                
                var peice = GetPuzzleCornerPeiceFor(place, mapping, puzzleCornerPlace);
                if (peice == null) throw new InvalidOperationException($"Method {place.Name} has no valid method to use.");
                buildList.Add((place, peice));
            }
            // Define the new class extending BaseClass
            AssemblyName assemblyName = classType.Assembly.GetName();
            AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            // Grant access to private/internal members of the originating assembly
            assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!,
                [classType.Assembly.GetName().Name!]));

            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
            var typeName = $"{classType.Name}{mapping.GetHashCode()}";
            TypeBuilder typeBuilder = moduleBuilder.DefineType(typeName, TypeAttributes.Sealed | TypeAttributes.Public, typeof(T));
            var constructors = classType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
            if (constructors.Length == 0) DefineConstructor(typeBuilder, classType, []);
            foreach (var constructor in constructors)
                DefineConstructor(typeBuilder, classType, [.. constructor.GetParameters().Select(p => p.ParameterType)]);
            foreach (var (Destination, Source) in buildList)
            {
                Type[] parameterTypes = [.. Destination.GetParameters().Select(p => p.ParameterType)];

                // Define the overriding method
                MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                    Destination.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.ReuseSlot,
                    Destination.ReturnType,
                    parameterTypes);
                if (Source.IsStatic)
                {
                    EmitForwardingIL(methodBuilder, Source, parameterTypes);
                }
                else
                {
                    CopyILBody(Source, methodBuilder);
                }

                CopyCustomAttributes(Source, methodBuilder);
                // Wire the override
                typeBuilder.DefineMethodOverride(methodBuilder, Destination);
            }
            var result = typeBuilder.CreateType();
            if (Cache) AssemblableMappings[fullName] = result;
            // Finalize
            return result;
        }

        private static MethodInfo GetPuzzleCornerPeiceFor(MethodInfo place, Dictionary<string, string> mapping, PuzzleCornerPiece puzzleCornerPlace)
        {
            if (place.DeclaringType == null) throw new InvalidOperationException($"Method {place.Name} DeclaringType is null.");
            if (!mapping.TryGetValue(puzzleCornerPlace.Pointer, out var mapValue)) throw new InvalidOperationException($"Method {place.Name} has no value set for {puzzleCornerPlace.Pointer}");
            if (!puzzleCornerPlace.KeyValues.TryGetValue(mapValue, out var mapTarget)) throw new InvalidOperationException($"Method {place.Name} has no target set for {puzzleCornerPlace.Pointer} with option {mapValue}");
           
            int lastDot = mapTarget.LastIndexOf('.');
            if (lastDot < 0)
            {
                mapTarget = $"{place.DeclaringType.FullName}.{mapTarget}";
                lastDot = mapTarget.LastIndexOf('.');
            }

            string typeName = mapTarget[..lastDot];
            string methodName = mapTarget[(lastDot + 1)..];


            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(typeName);
                if (type is null) continue;

                var method = type.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (method is not null
                    && method.ReturnType == place.ReturnType
                    && method.GetParameters()
                             .Select(p => p.ParameterType)
                             .SequenceEqual(place.GetParameters().Select(p => p.ParameterType)))
                    return method;
            }
            throw new InvalidOperationException($"Method {place.Name} has no valid static method match to use.");
        }

        public static T CreateInstance<T>(Dictionary<string, string> mapping, params object?[]? args) => (T)Activator.CreateInstance(Assemble<T>(mapping), args)!;
        public static T CreateInstanceForSystem<T>(Func<MethodInfo, object?[]?> getArgsFor, out Dictionary<string, string> bestCombination, params object?[]? constructorArgs) => CreateInstanceForSystem<T>(getArgsFor, [], 200, 2_000, out bestCombination, constructorArgs);
        public static T CreateInstanceForSystem<T>(Func<MethodInfo, object?[]?> getArgsFor, Dictionary<string, string> mapping, out Dictionary<string, string> bestCombination, params object?[]? constructorArgs) => CreateInstanceForSystem<T>(getArgsFor, mapping, 200, 2_000, out bestCombination, constructorArgs);
        /// <summary>
        /// Benchmarks every valid combination of puzzle pieces not already pinned
        /// in mapping, and returns an instance of whichever is fastest
        /// on this system.
        /// </summary>
        /// <param name="getArgsFor"> should provide arguments to test the method provided </param>        
        /// <param name="warmup">Iterations before timing begins.</param>
        /// <param name="iterations">Timed iterations per candidate.</param>
        /// <param name="constructorArgs">Args forwarded to the constructor.</param>
        public static T CreateInstanceForSystem<T>(Func<MethodInfo, object?[]?> getArgsFor, Dictionary<string, string> pinnedMapping, int warmup, int iterations, out Dictionary<string, string> bestCombination, params object?[]? constructorArgs)
        {
            /// <summary>
            /// Compiles a zero-arg Action that calls <paramref name="method"/> on
            /// <paramref name="instance"/> with fixed <paramref name="args"/>.
            /// Return values are kept alive to prevent JIT dead-code elimination.
            /// </summary>
            static Action BuildInvoker(object instance, MethodInfo method, object?[]? args)
            {
                var instanceExpr = Expression.Constant(instance);
                var argExprs = method.GetParameters()
                    .Select((p, i) => Expression.Constant(args?[i], p.ParameterType))
                    .ToArray<Expression>();

                Expression call = Expression.Call(instanceExpr, method, argExprs);

                // If the method returns a value, wrap in GC.KeepAlive to stop the JIT
                // from eliminating the call as a side-effect-free no-op
                if (method.ReturnType != typeof(void))
                {
                    var keepAlive = typeof(GC).GetMethod(nameof(GC.KeepAlive))!;
                    call = Expression.Block(
                        Expression.Call(keepAlive, Expression.Convert(call, typeof(object))));
                }

                return Expression.Lambda<Action>(call).Compile();
            }
            static IEnumerable<Dictionary<string, string>> CartesianProduct(Dictionary<string, List<string>> byKey)
            {
                IEnumerable<Dictionary<string, string>> seed = [[]];
                return byKey.Aggregate(seed, (acc, kv) =>
                    acc.SelectMany(existing =>
                        kv.Value.Select(value =>
                        {
                            var next = new Dictionary<string, string>(existing) { [kv.Key] = value };
                            return next;
                        })));
            }
            var classType = typeof(T);

            // Pre-compute args for each PuzzlePlace once — method signatures don't
            // change between combinations, only which piece fills them does
            var puzzlePlaces = classType.GetMethods()
                .Where(m => m.GetCustomAttribute<PuzzlePlace>() is not null)
                .Select(m => (Method: m, Args: getArgsFor(m)))
                .ToList();

            if (puzzlePlaces.Count == 0)
                throw new InvalidOperationException($"Type {classType.FullName} has no [{nameof(PuzzlePlace)}] methods to benchmark.");

            // Build combination space from free (un-pinned) keys
            var byKey = classType.GetMethods()
                .Select(m => m.GetCustomAttribute<PuzzlePeice>())
                .Where(a => a is not null)
                .GroupBy(a => a!.Key)
                .ToDictionary(g => g.Key, g => g.Select(a => a!.Value).Distinct().ToList());

            foreach (var key in pinnedMapping.Keys)
                byKey.Remove(key);

            var combinations = CartesianProduct(byKey)
                .Select(combo =>
                {
                    var merged = new Dictionary<string, string>(pinnedMapping);
                    foreach (var kv in combo) merged[kv.Key] = kv.Value;
                    return merged;
                })
                .ToList();

            if (combinations.Count == 0)
                combinations.Add(new Dictionary<string, string>(pinnedMapping));

            long bestTicks = long.MaxValue;
            T? bestInstance = default;
            bestCombination = [];
            foreach (var currentMapping in combinations)
            {
                Type assembled;
                try { assembled = Assemble<T>(currentMapping); }
                catch { continue; }

                var instance = (T)Activator.CreateInstance(assembled, constructorArgs)!;

                // Build a compiled delegate per PuzzlePlace on THIS assembled type.
                // Using compiled expressions avoids MethodInfo.Invoke overhead
                // skewing the benchmark — we want to measure the piece, not reflection.
                var invokers = puzzlePlaces
                    .Select(p =>
                    {
                        // Resolve the concrete (non-abstract) override on the assembled type
                        var concrete = assembled.GetMethod(
                            p.Method.Name,
                            p.Method.GetParameters().Select(x => x.ParameterType).ToArray())!;

                        return BuildInvoker(instance!, concrete, p.Args);
                    })
                    .ToList();

                // Warmup — let the JIT compile and settle each piece
                for (int i = 0; i < warmup; i++)
                    foreach (var invoke in invokers) invoke();

                // Timed run across ALL PuzzlePlaces for this combination
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                    foreach (var invoke in invokers) invoke();
                sw.Stop();

                if (sw.ElapsedTicks < bestTicks)
                {
                    bestTicks = sw.ElapsedTicks;
                    bestInstance = instance;
                    bestCombination = currentMapping;
                }
            }

            return bestInstance
                ?? throw new InvalidOperationException("No valid puzzle piece combination could be assembled.");
        }
        public static Dictionary<MethodInfo, List<MethodInfo>> GetJigSawPuzzle<T>()
        {
            var classType = typeof(T);
            var result = new Dictionary<MethodInfo, List<MethodInfo>>();
            var puzzlePlaces = classType.GetMethods()
                .Where(m => m.GetCustomAttribute<PuzzlePlace>() is not null);
            var puzzlePeices = classType.GetMethods()
                .Where(m => m.GetCustomAttribute<PuzzlePeice>() is not null);

            foreach (var place in puzzlePlaces)
            {
                result[place] = new List<MethodInfo>();
                foreach (var candidate in puzzlePeices)
                {
                    var puzzlePeice = candidate.GetCustomAttribute<PuzzlePeice>();
                    if (puzzlePeice == null) continue;
                    if (puzzlePeice.Pointer != place.Name) continue;
                    if (string.IsNullOrWhiteSpace(puzzlePeice.Key)) continue;
                    if (string.IsNullOrWhiteSpace(puzzlePeice.Value)) continue;
                    if (place.DeclaringType == candidate.DeclaringType
                        && place.ReturnType == candidate.ReturnType
                        && place.GetParameters().Select(p => p.ParameterType).SequenceEqual(candidate.GetParameters().Select(p => p.ParameterType)))
                        result[place].Add(candidate);
                }
            }
            return result;
        }

        private static readonly OpCode[] SingleByteOpcodes = BuildSingleByteTable();
        private static readonly OpCode[] MultiByteOpcodes = BuildMultiByteTable();

        private static OpCode[] BuildSingleByteTable()
        {
            var table = new OpCode[256];
            foreach (var f in typeof(OpCodes).GetFields())
            {
                var op = (OpCode)f.GetValue(null)!;
                if (op.Size == 1) table[op.Value & 0xFF] = op;
            }
            return table;
        }

        private static OpCode[] BuildMultiByteTable()
        {
            var table = new OpCode[256];
            foreach (var f in typeof(OpCodes).GetFields())
            {
                var op = (OpCode)f.GetValue(null)!;
                if (op.Size == 2) table[op.Value & 0xFF] = op;
            }
            return table;
        }

        private static (OpCode op, int size) ReadOpCode(byte[] il, int offset) =>
            il[offset] == 0xFE
                ? (MultiByteOpcodes[il[offset + 1]], 2)
                : (SingleByteOpcodes[il[offset]], 1);

        private static bool IsBranch(OpCode op) =>
            op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch;

        private static int OperandSize(OpCode op) => op.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineR => 4,
            OperandType.ShortInlineVar => 1,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineField => 4,
            OperandType.InlineI => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineString => 4,
            OperandType.InlineSwitch => 4,
            OperandType.InlineType => 4,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 => 8,
            OperandType.InlineR => 8,
            _ => 0
        };
        /// <summary>
        /// Emits forwarding IL into <paramref name="destination"/> that loads
        /// every parameter (skipping 'this') and calls <paramref name="staticTarget"/> directly.
        /// <c>ldarg_1 … ldarg_N → call static → ret</c>
        /// </summary>
        private static void EmitForwardingIL(
            MethodBuilder destination,
            MethodInfo staticTarget,
            Type[] destinationParamTypes)
        {
            var targetParams = staticTarget.GetParameters();
            if (targetParams.Length != destinationParamTypes.Length)
                throw new InvalidOperationException(
                    $"PuzzleCornerPiece forwarding mismatch: " +
                    $"destination has {destinationParamTypes.Length} parameter(s) but " +
                    $"'{staticTarget.DeclaringType?.Name}.{staticTarget.Name}' has {targetParams.Length}. " +
                    "The external static method must have the same parameter list as the PuzzlePlace.");

            var il = destination.GetILGenerator();

            // ldarg_0 = 'this' in the instance override — skip it.
            // ldarg_1..N map to the real parameters.
            for (int i = 0; i < destinationParamTypes.Length; i++)
                il.Emit(OpCodes.Ldarg, i + 1);

            il.Emit(OpCodes.Call, staticTarget); // direct static call; JIT will inline
            il.Emit(OpCodes.Ret);
        }
        private static void CopyILBody(MethodInfo source, MethodBuilder target)
        {
            var body = source.GetMethodBody()!;
            var il = target.GetILGenerator();
            var module = source.Module;

            foreach (var local in body.LocalVariables)
                il.DeclareLocal(local.LocalType, local.IsPinned);

            var rawIL = body.GetILAsByteArray()!;
            var labels = new Dictionary<int, Label>();

            // First pass — declare a Label for every branch target
            int offset = 0;
            while (offset < rawIL.Length)
            {
                var (op, opcodeSize) = ReadOpCode(rawIL, offset);
                offset += opcodeSize;

                if (IsBranch(op))
                {
                    int targetLabel = op.OperandType == OperandType.ShortInlineBrTarget
                        ? offset + 1 + (sbyte)rawIL[offset]
                        : offset + 4 + BitConverter.ToInt32(rawIL, offset);

                    if (!labels.ContainsKey(targetLabel))
                        labels[targetLabel] = il.DefineLabel();
                }

                offset += OperandSize(op);
            }

            // Second pass — emit
            offset = 0;
            while (offset < rawIL.Length)
            {
                if (labels.TryGetValue(offset, out var label))
                    il.MarkLabel(label);

                var (op, opcodeSize) = ReadOpCode(rawIL, offset);
                offset += opcodeSize;

                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        il.Emit(op);
                        break;
                    case OperandType.InlineMethod:
                        {
                            int token = BitConverter.ToInt32(rawIL, offset); offset += 4;
                            var method = module.ResolveMethod(token)!;
                            if (method is ConstructorInfo ctor) il.Emit(op, ctor);
                            else il.Emit(op, (MethodInfo)method);
                            break;
                        }
                    case OperandType.InlineField:
                        {
                            int token = BitConverter.ToInt32(rawIL, offset); offset += 4;
                            il.Emit(op, module.ResolveField(token)!);
                            break;
                        }
                    case OperandType.InlineType:
                        {
                            int token = BitConverter.ToInt32(rawIL, offset); offset += 4;
                            il.Emit(op, module.ResolveType(token)!);
                            break;
                        }
                    case OperandType.InlineString:
                        {
                            int token = BitConverter.ToInt32(rawIL, offset); offset += 4;
                            il.Emit(op, module.ResolveString(token));
                            break;
                        }
                    case OperandType.InlineI:
                        {
                            int val = BitConverter.ToInt32(rawIL, offset); offset += 4;
                            il.Emit(op, val);
                            break;
                        }
                    case OperandType.InlineI8:
                        {
                            long val = BitConverter.ToInt64(rawIL, offset); offset += 8;
                            il.Emit(op, val);
                            break;
                        }
                    case OperandType.InlineR:
                        {
                            double val = BitConverter.ToDouble(rawIL, offset); offset += 8;
                            il.Emit(op, val);
                            break;
                        }
                    case OperandType.ShortInlineR:
                        {
                            float val = BitConverter.ToSingle(rawIL, offset); offset += 4;
                            il.Emit(op, val);
                            break;
                        }
                    case OperandType.ShortInlineI:
                        il.Emit(op, (sbyte)rawIL[offset++]);
                        break;
                    case OperandType.InlineBrTarget:
                        {
                            int abs = offset + 4 + BitConverter.ToInt32(rawIL, offset); offset += 4;
                            il.Emit(op, labels[abs]);
                            break;
                        }
                    case OperandType.ShortInlineBrTarget:
                        {
                            int abs = offset + 1 + (sbyte)rawIL[offset]; offset += 1;
                            il.Emit(op, labels[abs]);
                            break;
                        }
                    case OperandType.InlineSwitch:
                        {
                            int count = BitConverter.ToInt32(rawIL, offset); offset += 4;
                            int baseOff = offset + count * 4;
                            var targets = new Label[count];
                            for (int i = 0; i < count; i++)
                            {
                                int abs = baseOff + BitConverter.ToInt32(rawIL, offset); offset += 4;
                                if (!labels.TryGetValue(abs, out var switchLabel))
                                {
                                    switchLabel = il.DefineLabel();
                                    labels[abs] = switchLabel;
                                }
                                targets[i] = switchLabel;
                            }
                            il.Emit(op, targets);
                            break;
                        }
                    case OperandType.InlineVar:
                        {
                            short idx = BitConverter.ToInt16(rawIL, offset); offset += 2;
                            il.Emit(op, idx);
                            break;
                        }
                    case OperandType.ShortInlineVar:
                        il.Emit(op, rawIL[offset++]);
                        break;
                    case OperandType.InlineSig:
                        offset += 4;
                        break;
                }
            }
        }
    }
}
