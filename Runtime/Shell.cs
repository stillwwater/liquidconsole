using System;
using System.Text;
using System.Collections.Generic;
using System.Reflection;

// Allows the shell to be used in non-unity projects
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace Liquid.Console
{
    [AttributeUsage(AttributeTargets.Method)]
    public class Command : Attribute {
        public readonly string usage;
        public readonly string name;

        public Command(string usage = null, string name = null) {
            this.usage = usage;
            this.name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class ConVar : Attribute {
        public readonly string usage;
        public readonly string name;

        public ConVar(string usage = null, string name = null) {
            this.usage = usage;
            this.name = name;
        }
    }

    public static class Shell {
        public delegate bool ArgParser(string input, out object val);

        [AttributeUsage(AttributeTargets.Method | AttributeTargets.Field)]
        public class Hidden : Attribute {}

        public struct Line {
            public string value;
            public int color;
        }

        struct Function {
            internal enum Type { Command, Variable, Hidden, Alias }

            internal object target;
            internal MethodInfo method;
            internal ParameterInfo[] signature;
            internal string usage;
            internal Function.Type type;
        }

        struct Token {
            internal enum Type { Literal, Eval };
            internal Token.Type type;
            internal string value;
            internal string rest;
            internal bool eol;
        }

        struct StackFrame {
            internal string func;
            internal List<string> arguments;

            internal StackFrame(string func, int reserve = 0) {
                this.func = func;
                this.arguments = new List<string>(reserve);
            }
        }

        // The buffer contains messages buffered by the shell. The buffer should
        // be cleared once the messages are flushed.
        public static readonly List<Line> buffer = new List<Line>();

        enum ConError {
            UnsupportedType,
            UndefinedLocal,
            UnexpectedEOF,
            UndefinedField,
            UndefinedMethod,
            StaticField,
            ArgType,
            RequiredArg,
            NoStackFrame,
            ReturnType,
            VoidReturn,
            Destroyed,
        };

        const BindingFlags Flags = BindingFlags.NonPublic
                                 | BindingFlags.Public
                                 | BindingFlags.Static
                                 | BindingFlags.Instance
                                 | BindingFlags.IgnoreCase;

        // The executing call stack.
        static readonly List<StackFrame> callstack = new List<StackFrame>();

        // The shell envirornment.
        static readonly Dictionary<string, Function> locals
            = new Dictionary<string, Function>();

        static readonly Dictionary<Type, ArgParser> parsers
            = new Dictionary<Type, ArgParser>() {
                { typeof(string), ParseString },
                { typeof(int), ParseInt },
                { typeof(float), ParseFloat },
                { typeof(bool), ParseBool },
                { typeof(int?), ParseNullable<int>(ParseInt) },
                { typeof(float?), ParseNullable<float>(ParseFloat) },
                { typeof(bool?), ParseNullable<bool>(ParseBool) },
            };

        // Only access inside a command method.
        public static int ArgCount => callstack[callstack.Count - 1].arguments.Count;

        // Initializes the shell by adding some default commands.
        // Optionally adds extension commands.
        public static void Init(bool math = false) {
            Module(typeof(Shell));
            if (math) InitMathExtension();
        }

        // Add math and logic commands.
        static void InitMathExtension() {
            Func(typeof(Shell), "if", "for",
                 "add", "sub", "mul", "div",
                 "min", "max", "floor", "ceil",
                 "lt", "lte", "gt", "gte", "eq", "ne", "not");
            // TODO: Group logical operators in op group since only aliases are used
            Shell.Eval(@"alias + add; alias - sub; alias * mul; alias / div;
                         alias < lt; alias <= lte; alias > gt; alias >= gte;
                         alias = eq; alias != ne");
        }

        // Removes all commands.
        public static void Dispose() {
            locals.Clear();
            buffer.Clear();
            callstack.Clear();
        }

        // Adds instance and static methods and fields marked with the
        // [Command] and [ConVar] attributes.
        public static void Module(object target)
            => AddModule(target.GetType(), target);

        // Adds static methods marked with the [Command] and [ConVar]
        // attributes.
        public static void Module(Type type) => AddModule(type, null);

        static void AddModule(Type type, object target) {
            var flags = target == null ? Flags & ~BindingFlags.Instance : Flags;

            foreach (var method in type.GetMethods(flags)) {
                var attr = method.GetCustomAttribute<Command>();
                if (attr != null) {
                    AddFunc(method, target, attr.name);
                }
            }

            foreach (var field in type.GetFields(flags)) {
                var attr = field.GetCustomAttribute<ConVar>();
                if (attr != null) {
                    AddVar(field, target, attr.name);
                }
            }
        }

        // Adds a command by finding the method in the target object.
        // The name of the method is not case sensitive.
        public static void Func(object target, string method) {
            var type = target.GetType();
            var info = type.GetMethod(method, Flags);

            if (info == null) {
                Fail(ConError.UndefinedMethod, type, method);
                return;
            }
            AddFunc(info, target, method);
        }

        // Adds multiple commands at once.
        public static void Func(object target, params string[] methods) {
            foreach (var method in methods) {
                Func(target, method);
            }
        }

        // Adds a command by finding the static method in the type.
        // Should only be used with static methods.
        public static void Func(Type type, string staticMethod) {
            var info = type.GetMethod(staticMethod, Flags);
            if (info == null) {
                Fail(ConError.UndefinedMethod, type, staticMethod);
                return;
            }
            AddFunc(info, null, staticMethod);
        }

        // Adds multuple commands at once.
        public static void Func(Type type, params string[] staticMethod) {
            foreach (var method in staticMethod) {
                Func(type, method);
            }
        }

        // Registers a command. By default the command will have the same name
        // as the method given (case insensitive).
        public static void Func(Action fn, string name = null)
            => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0>(Action<T0> fn, string name = null)
            => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0, T1>(Action<T0, T1> fn, string name = null)
            => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0, T1, T2>(Action<T0, T1, T2> fn, string name = null)
            => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0, T1, T2, T3>(
            Action<T0, T1, T2, T3> fn, string name = null)
                => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0>(System.Func<T0> fn, string name = null)
            => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0, T1>(System.Func<T0, T1> fn, string name = null)
            => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0, T1, T2>(
            System.Func<T0, T1, T2> fn, string name = null)
                => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0, T1, T2, T3>(
            System.Func<T0, T1, T2, T3> fn, string name = null)
                => AddFunc(fn.Method, fn.Target, name);

        public static void Func<T0, T1, T2, T3, T4>(
            System.Func<T0, T1, T2, T3, T4> fn, string name = null)
                => AddFunc(fn.Method, fn.Target, name);

        static void AddFunc(MethodInfo method, object target, string name) {
            var sign = method.GetParameters();
            var func = new Function {
                target = target,
                method = method,
                signature = sign,
                type = Function.Type.Command,
            };
            var command = method.GetCustomAttribute<Command>();
            if (command != null) {
                name = name ?? command.name;
                func.usage = command.usage;
            }
            if (method.GetCustomAttribute<Hidden>() != null) {
                func.type = Function.Type.Hidden;
            }
            AddFunc(func, (name ?? method.Name).ToLowerInvariant());
        }

        static void AddFunc(Function func, string name) {
            // Make sure we can parse the required items
            foreach (var param in func.signature) {
                if (!parsers.ContainsKey(param.ParameterType)) {
                    Fail(ConError.UnsupportedType, param.ParameterType);
                    return;
                }
            }
            if (locals.ContainsKey(name)) {
                locals[name] = func;
                return;
            }
            locals.Add(name, func);
        }

        // Add a reference to a variable or field. The fieldName should match the field's
        // name exactly.
        public static void Var(object target, string field) {
            var type = target.GetType();
            var info = type.GetField(field, Flags);

            if (info == null) {
                Fail(ConError.UndefinedField, type, field);
                return;
            }

            if (info.IsStatic) {
                Fail(ConError.StaticField, type, field);
                return;
            }
            AddVar(info, target);
        }

        public static void Var(object target, params string[] fields) {
            foreach (var field in fields) {
                Var(target, field);
            }
        }

        // Add a reference to a static field. staticField must match the field's name
        // exactly.
        public static void Var(Type type, string staticField) {
            var info = type.GetField(staticField, Flags);
            if (info == null) {
                Fail(ConError.UndefinedField, type, staticField);
                return;
            }
            AddVar(info, null);
        }

        public static void Var(Type type, params string[] fields) {
            foreach (var field in fields) {
                Var(type, field);
            }
        }

        static void AddVar(FieldInfo field, object target, string name = null) {
            Action getset = () => {
                if (ArgCount == 0) {
                    Print(field.GetValue(target).ToString());
                    return;
                }
                field.SetValue(target, Arg(0, field.FieldType));
            };
            var func = new Function {
                method = getset.Method,
                target = target,
                signature = getset.Method.GetParameters(),
                type = Function.Type.Variable,
            };
            var conVar = field.GetCustomAttribute<ConVar>();

            if (conVar != null) {
                name = name ?? conVar.name;
                func.usage = conVar.usage;
            }

            if (field.GetCustomAttribute<Hidden>() != null) {
                func.type = Function.Type.Hidden;
            }
            AddFunc(func, (name ?? field.Name).ToLowerInvariant());
        }

        // Unregister a command or variable
        public static void Remove(string name) {
            locals.Remove(name.ToLowerInvariant());
        }

        // Returns an argument for the current command. Arguments start at index 0.
        // If optional is set to true, the argument will have a default value
        // assigned if it's not present.
        public static T Arg<T>(int index, bool optional = false) {
            var val = Arg(index, typeof(T), optional);
            if (val == null) {
                return default(T);
            }
            return (T)val;
        }

        public static object Arg(int index, Type type, bool optional = false) {
            object val;
            if (!StackLocal(index, type, optional, out val)) {
                return null;
            }
            return val;
        }

        static bool StackLocal(int index, Type type, bool optional, out object arg) {
            int offset = (callstack.Count - 1);
            ArgParser parser;
            if (!parsers.TryGetValue(type, out parser)) {
                arg = null;
                Fail(ConError.UnsupportedType, type);
                return false;
            }
            if (offset < 0) {
                Fail(ConError.NoStackFrame);
                arg = null;
                return false;
            }
            var frame = callstack[offset];

            if (index < 0 || index >= frame.arguments.Count) {
                arg = null;
                if (!optional && Nullable.GetUnderlyingType(type) == null) {
                    Fail(ConError.RequiredArg, frame.func, index, type);
                    return false;
                }
                return true;
            }
            var str = frame.arguments[index];
            if (!parser(str, out arg)) {
                Fail(ConError.ArgType, frame.func, str, type);
                return false;
            }
            return true;
        }

        // Pops and returns the return value of a command from the buffer
        public static T PopReturn<T>() {
            if (buffer.Count == 0) {
                Fail(ConError.VoidReturn);
                return default(T);
            }
            ArgParser parser;

            if (!parsers.TryGetValue(typeof(T), out parser)) {
                Fail(ConError.UnsupportedType, typeof(T));
                return default(T);
            }

            object val;
            if (!parser(PopReturn(), out val)) {
                Fail(ConError.ReturnType, typeof(T));
                return default(T);
            }
            return (T)val;
        }

        static string PopReturn() {
            if (buffer.Count == 0) {
                return null;
            }
            var top = buffer[buffer.Count - 1];
            buffer.RemoveAt(buffer.Count - 1);
            return top.value;
        }

        static bool Call(string name) {
            Function func;
            if (!locals.TryGetValue(name.ToLowerInvariant(), out func)) {
                Fail(ConError.UndefinedLocal, name);
                return false;
            }

#if UNITY_5_3_OR_NEWER
            // If the component has been destroyed this command should be removed
            if (func.target is MonoBehaviour && (MonoBehaviour)func.target == null) {
                Fail(ConError.Destroyed, name, func.target.GetType());
                Remove(name);
                return false;
            }
#endif
            var args = new object[func.signature.Length];

            for (int i = 0; i < args.Length; ++i) {
                var param = func.signature[i];
                if (!StackLocal(i, param.ParameterType, param.IsOptional, out args[i])) {
                    return false;
                }

                if (args[i] == null && param.IsOptional) {
                    args[i] = param.DefaultValue;
                }
            }

            // Commands that return a value get the value printed after they run
            var ret = func.method.Invoke(func.target, args);
            if (ret != null && func.method.ReturnType != typeof(void)) {
                Print(ret);
            }
            return true;
        }

        // Evaluates a command.
        public static bool Eval(string input) {
            bool ok = true;
            while (ok && input != null && input != "") {
                var stack = Parse(ref input);
                if (stack == null) return false;
                if (stack.Count == 0 || stack[0] == "") return true;

                // Push arguments onto the call stack
                var frame = new StackFrame(stack[0], stack.Count - 1);
                for (int i = 1; i < stack.Count; ++i) {
                    frame.arguments.Add(stack[i]);
                }
                callstack.Add(frame);
                ok = Call(stack[0]);

                // Restore the stack
                callstack.RemoveAt(callstack.Count - 1);
            }
            return ok;
        }

        // Add a parser to handle objects of type T
        public static void AddParser<T>(ArgParser parser) {
            if (parsers.ContainsKey(typeof(T))) {
                parsers[typeof(T)] = parser;
                return;
            }
            parsers.Add(typeof(T), parser);
        }

        // Convenience method for joining command arguments into one string.
        public static string JoinArgs(int start = 0, string sep = " ") {
            if (ArgCount == 0) return "";
            if (ArgCount == 1) return Arg<string>(0);
            var str = new StringBuilder();
            for (int i = 0; i < ArgCount; ++i) {
                if (i > 0) {
                    str.Append(sep);
                }
                str.Append(Arg<string>(i));
            }
            return str.ToString();
        }

        public static void Out(string value, int color = 7) {
            buffer.Add(new Line { value = value.ToString(), color = color });
        }

        public static void Print() => Print("");

        public static void Print(object value) => Out(value.ToString(), 7);

        public static void Error(object value) => Out(value.ToString(), 1);

        public static void Warning(object value) => Out(value.ToString(), 2);

        public static void PrintFmt(string format, params object[] args)
            => Out(string.Format(format, args), 7);

        public static void ErrorFmt(string format, params object[] args)
            => Out(string.Format(format, args), 1);

        public static void WarningFmt(string format, params object[] args)
            => Out(string.Format(format, args), 2);

        static bool ParseString(string input, out object val) {
            val = input;
            return true;
        }

        static bool ParseInt(string input, out object val) {
            float f;
            bool ok = float.TryParse(input, out f);
            val = (int)f;
            return ok;
        }

        static bool ParseFloat(string input, out object val) {
            float f;
            bool ok = float.TryParse(input, out f);
            val = f;
            return ok;
        }

        static bool ParseBool(string input, out object val) {
            if (input == "0" || input.ToLowerInvariant() == "false") {
                val = false;
                return true;
            }
            if (input == "1" || input.ToLowerInvariant() == "true") {
                val = true;
                return true;
            }
            val = null;
            return false;
        }

        static ArgParser ParseNullable<T>(ArgParser parser) where T : struct
            => (string input, out object val) => {
                if (parser(input, out object v)) {
                    val = new Nullable<T>((T)v);
                    return true;
                }
                val = null;
                return false;
            };

        static List<string> Parse(ref string input) {
            var stack = new List<string>();
            var buf = new StringBuilder();

            while (input != "") {
                var optionalTok = EatNext(input, buf);
                if (!optionalTok.HasValue) {
                    return null;
                }
                var tok = optionalTok.Value;

                switch (tok.type) {
                    case Token.Type.Literal:
                        stack.Add(tok.value);
                        break;

                    case Token.Type.Eval:
                        if (!Eval(tok.value)) return null;
                        if (buffer.Count == 0) {
                            Fail(ConError.VoidReturn);
                            return null;
                        }
                        stack.Add(PopReturn());
                        break;
                }

                input = tok.rest;
                if (tok.eol) break;
            }
            return stack;
        }

        static Token? EatNext(string input, StringBuilder buf) {
            buf.Clear();
            if (input == "") {
                return new Token { eol = true, rest = "" };
            }
            var parse = new Parser {
                input = input,
                tok = new StringBuilder(),
                type = Token.Type.Literal
            };

            for (int i = 0; i < input.Length; ++i) {
                parse.pos = i;
                parse.ch = input[i];

                switch (parse.ch) {
                    case '(':
                    case '{': parse.Open(); break;

                    case ')':
                    case '}':
                        if (parse.Close()) return parse.GetToken();
                        break;

                    case '"':
                        if (parse.Quote()) return parse.GetToken();
                        break;

                    case ':': parse.Var(); break;

                    case ' ':
                        if (parse.Space()) return parse.GetToken();
                        break;

                    case ';':
                        if (parse.Semi()) return parse.GetToken();
                        break;

                    case '\r':
                    case '\n': break;

                    default:
                        parse.tok.Append(parse.ch);
                        break;
                }
            }
            if (parse.EofError()) {
                return null;
            }
            return new Token {
                type = parse.type,
                value = parse.tok.ToString(),
                rest = ""
            };
        }

        static string LocalSignature(string name, in Function func) {
            var sign = new StringBuilder();
            if (func.type == Function.Type.Variable) {
                sign.Append("var ");
            } else if (func.type == Function.Type.Alias) {
                sign.Append("alias ");
            }
            sign.Append(name);
            sign.Append(' ');

            for (int i = 0; i < func.signature.Length; ++i) {
                var param = func.signature[i];
                if (i > 0) sign.Append(' ');

                sign.Append(param.ParameterType.Name);
                sign.Append(':');
                sign.Append(param.Name);

                if (param.IsOptional) {
                    sign.Append('?');
                }
            }
            return sign.ToString();
        }

        static string GetErrorString(ConError error) {
            switch (error) {
                case ConError.UnsupportedType: return "Unsupported parameter type {0}";
                case ConError.UndefinedLocal: return "undefined local '{0}'";
                case ConError.UnexpectedEOF: return "expected '{0}' before EOF";
                case ConError.UndefinedField: return "'{0}' has no field named '{1}'";
                case ConError.UndefinedMethod: return "'{0}' has no method named '{1}'";
                case ConError.ArgType: return "({0}) '{1}' cannot be converted to {2}";
                case ConError.RequiredArg: return "({0}) parameter #{1} ({2}) is not optional";
                case ConError.ReturnType: return "'{0}' cannot be converted to {1}";
                case ConError.VoidReturn: return "expected a return value";
                case ConError.Destroyed: return "({0}) the Component '{1}' has been destroyed";
                case ConError.NoStackFrame:
                    return "cannot get argument outside of a command method.";
                case ConError.StaticField:
                    return "static field '{1}' cannot have a target object '{0}'";

                default: return null;
            }
        }

        static void Fail(ConError error, params object[] args) {
            ErrorFmt(string.Concat("error: ", GetErrorString(error)), args);
        }

        [Command("prints usage for a given command")]
        static void Help(string command = null) {
            if (command == null) {
                foreach (var local in locals) {
                    if (local.Value.type == Function.Type.Command) {
                        Print(local.Key);
                    }
                }
                return;
            }
            string name = command.ToLowerInvariant();

            Function func;
            if (!locals.TryGetValue(name, out func)) {
                Fail(ConError.UndefinedLocal, command);
                return;
            }
            Out(LocalSignature(name, func), 4);
            if (func.usage != null) {
                Print(func.usage);
            }
        }

        [Command("defines a new variable")]
        static void Let(string variable, string val = null) {
            if (val == null) val = "0";
            Func(() => {
                if (ArgCount == 0) {
                    Print(val);
                    return;
                }
                val = Arg<string>(0);
            }, variable.ToLowerInvariant());
        }

        [Command("undefines a command or variable")]
        static void Unlet(string local) {
            locals.Remove(local.ToLowerInvariant());
        }

        [Command("gives a command or variable an alias")]
        static void Alias(string alias, string name) {
            name = name.ToLowerInvariant();
            Action command = () => {
                Eval($"{name} {JoinArgs()}");
            };
            var func = new Function {
                method = command.Method,
                target = command.Target,
                signature = command.Method.GetParameters(),
                type = Function.Type.Alias,
            };
            AddFunc(func, alias.ToLowerInvariant());
        }

        [Command("prints a message to the console", "print")]
        static void CPrint() => Out(JoinArgs());

        [Command("prints a message to the console with an optional color")]
        static void Puts(string value, int color = 7) => Out(value, color);

        [Command("concatenates strings together")]
        static string Join() => JoinArgs(0, "");

        static void If(bool exp, string ifTrue, string ifFalse = null) {
            if (exp)
                Eval(ifTrue);
            else if (ifFalse != null)
                Eval(ifFalse);
        }

        static void For(string variable, int min, int max, string block) {
            for (int i = min; i < max; ++i) {
                var index = i;
                Func(() => Print(index), variable);
                if (!Eval(block)) {
                    break;
                }
            }
        }

        static float Min(float a, float b) => (float)Math.Min(a, b);
        static float Max(float a, float b) => (float)Math.Min(a, b);
        static float Floor(float a) => (float)Math.Floor(a);
        static float Ceil(float a) => (float)Math.Ceiling(a);

        [Hidden] static float Add(float a, float b) => a + b;
        [Hidden] static float Sub(float a, float b) => a - b;
        [Hidden] static float Mul(float a, float b) => a * b;
        [Hidden] static float Div(float a, float b) => a / b;

        [Hidden] static bool Eq(string a, string b) => a == b;
        [Hidden] static bool Ne(string a, string b) => a != b;
        [Hidden] static bool Lt(float a, float b) => a < b;
        [Hidden] static bool Lte(float a, float b) => a <= b;
        [Hidden] static bool Gt(float a, float b) => a > b;
        [Hidden] static bool Gte(float a, float b) => a >= b;
        [Hidden] static bool Not(bool a) => !a;

        struct Parser {
            internal string input;
            internal StringBuilder tok;
            internal Token.Type type;
            internal char ch;
            internal int pos;

            int paren;
            int brace;
            bool quote;
            bool eol;

            internal bool OuterScope => paren == 0 && brace == 0 && !quote;

            internal Token GetToken() {
                return new Token {
                    type = type,
                    eol = eol,
                    value = tok.ToString(),
                    rest = input.Substring(pos + 1)
                };
            }

            internal bool EofError() {
                if (quote) {
                    Fail(ConError.UnexpectedEOF, '"');
                    return true;
                }
                if (paren > 0) {
                    Fail(ConError.UnexpectedEOF, ')');
                    return true;
                }
                if (brace > 0) {
                    Fail(ConError.UnexpectedEOF, '}');
                    return true;
                }
                return false;
            }

            internal bool Space() {
                if (OuterScope) {
                    return tok.Length > 0;
                }
                tok.Append(ch);
                return false;
            }

            internal void Var() {
                if (OuterScope) {
                    type = Token.Type.Eval;
                    return;
                }
                tok.Append(ch);
            }

            internal bool Semi() {
                if (OuterScope) {
                    eol = true;
                    return true;
                }
                tok.Append(ch);
                return false;
            }

            internal bool Quote() {
                // assert type is literal
                if (paren > 0 || brace > 0) {
                    tok.Append(ch);
                    return false;
                }
                quote = !quote;
                return !quote;
            }

            internal void Open() {
                if (quote) {
                    tok.Append(ch);
                    return;
                }
                switch (ch) {
                    case '(':
                        if (brace > 0) break;
                        type = Token.Type.Eval;
                        if (paren++ == 0) return;
                        break;
                    case '{':
                        if (paren > 0) break;
                        if (brace++ == 0) return;
                        break;
                }
                tok.Append(ch);
            }

            internal bool Close() {
                if (quote) {
                    tok.Append(ch);
                    return false;
                }
                switch (ch) {
                    case ')':
                        if (brace > 0) break;
                        if (--paren == 0) return true;
                        break;
                    case '}':
                        if (paren > 0) break;
                        if (--brace == 0) return true;
                        break;
                }
                tok.Append(ch);
                return false;
            }
        }
    }
} // namespace Liquid.Console
