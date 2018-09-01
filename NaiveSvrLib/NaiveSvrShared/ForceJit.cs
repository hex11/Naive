using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Naive.HttpSvr
{
    public class ForceJit
    {
        public static JitResult ForceJitAssembly(params Assembly[] assemblies)
        {
            var result = new JitResult();
            foreach (var assembly in assemblies.Distinct()) {
                var types = assembly.GetTypes();

                foreach (var type in types) {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static;
                    foreach (var item in type.GetConstructors(flags)) {
                        if (!CanJit(item))
                            continue;
                        if (ForceJitMethod(item))
                            result.Ctors++;
                        else
                            result.Errors++;
                    }
                    foreach (var item in type.GetMethods(flags)) {
                        if (!CanJit(item))
                            continue;
                        if (ForceJitMethod(item))
                            result.Methods++;
                        else
                            result.Errors++;
                    }
                }
                result.Types += types.Length;
                result.Assemblies++;
            }

            return result;
        }

        private static bool CanJit(MethodBase methodBase)
        {
            const MethodImplAttributes cantAttrs = MethodImplAttributes.Unmanaged | MethodImplAttributes.InternalCall | MethodImplAttributes.PreserveSig;
            return !(methodBase.IsAbstract
                || methodBase.ContainsGenericParameters
                || (methodBase.MethodImplementationFlags & cantAttrs) != 0);
        }

        public static bool ForceJitMethod(MethodBase methodBase)
        {
            try {
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(methodBase.MethodHandle);
                //Logging.info("ForceJit success: " + str);
                return true;
            } catch (Exception e) {
                var str = methodBase.DeclaringType.AssemblyQualifiedName + " " + methodBase.Name;
                Logging.exception(e, Logging.Level.Warning, "ForceJit error: " + str);
                return false;
            }
        }

        public class JitResult
        {
            public int Assemblies;
            public int Types;
            public int Ctors;
            public int Methods;
            public int Errors;
        }
    }
}
