using System;
using System.Collections.Generic;
using System.Linq;
using Deltin.Deltinteger.LanguageServer;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.Lobby;
using Deltin.Deltinteger.I18n;
using Deltin.Deltinteger.Debugger;
using Deltin.Deltinteger.Compiler;
using Deltin.Deltinteger.Compiler.SyntaxTree;

namespace Deltin.Deltinteger.Parse
{
    public class DeltinScript : IScopeProvider, IScopeAppender
    {
        private FileGetter FileGetter { get; }
        private Importer Importer { get; }
        public Diagnostics Diagnostics { get; }
        public ScriptTypes Types { get; }
        public Scope PlayerVariableScope { get; set; }
        public Scope GlobalScope { get; }
        public Scope RulesetScope { get; }
        public VarCollection VarCollection { get; } = new VarCollection();
        public SubroutineCollection SubroutineCollection { get; } = new SubroutineCollection();
        private List<Var> rulesetVariables { get; } = new List<Var>();
        public VarIndexAssigner DefaultIndexAssigner { get; } = new VarIndexAssigner();
        public TranslateRule InitialGlobal { get; private set; }
        public TranslateRule InitialPlayer { get; private set; }
        private readonly OutputLanguage Language;
        public readonly bool OptimizeOutput;
        private List<IComponent> Components { get; } = new List<IComponent>();
        private List<InitComponent> InitComponent { get; } = new List<InitComponent>();
        public DebugVariableLinkCollection DebugVariables { get; } = new DebugVariableLinkCollection();

        public DeltinScript(TranslateSettings translateSettings)
        {
            FileGetter = translateSettings.FileGetter;
            Diagnostics = translateSettings.Diagnostics;
            Language = translateSettings.OutputLanguage;
            OptimizeOutput = translateSettings.OptimizeOutput;

            Types = new ScriptTypes(this);
            Types.GetDefaults();
            GlobalScope = Scope.GetGlobalScope(this);

            RulesetScope = GlobalScope.Child();
            RulesetScope.PrivateCatch = true;
            Types.AddTypesToScope(GlobalScope);

            Importer = new Importer(this, FileGetter, translateSettings.Root.Uri);
            Importer.CollectScriptFiles(translateSettings.Root);
            
            Translate();
            if (!Diagnostics.ContainsErrors())
                try
                {
                    ToWorkshop(translateSettings.AdditionalRules);
                }
                catch (Exception ex)
                {
                    WorkshopCode = "An exception was thrown while translating to workshop.\r\n" + ex.ToString();
                }
            
            foreach (IComponent component in Components)
                if (component is IDisposable disposable)
                    disposable.Dispose();
        }

        public T GetComponent<T>() where T: IComponent, new()
        {
            foreach (IComponent component in Components)
                if (component is T t)
                    return t;
            
            T newT = new T();
            newT.DeltinScript = this;

            for (int i = InitComponent.Count - 1; i >= 0; i--)
                if (typeof(T) == InitComponent[i].ComponentType)
                {
                    InitComponent[i].Apply(newT);
                    InitComponent.RemoveAt(i);
                }
            
            Components.Add(newT);
            newT.Init();

            return newT;
        }

        public bool IsComponent<T>() where T: IComponent => Components.Any(component => component is T);
        public bool IsComponent<T>(out T component) where T: IComponent
        {
            foreach (IComponent iterateComponent in Components)
                if (iterateComponent is T t)
                {
                    component = t;
                    return true;
                }
            component = default(T);
            return false;
        }

        public void ExecOnComponent<T>(Action<T> apply) where T: IComponent
        {
            if (IsComponent<T>(out T existing))
                apply.Invoke(existing);
            else
                InitComponent.Add(new InitComponent(typeof(T), component => apply.Invoke((T)component)));
        }

        private List<RuleAction> rules { get; } = new List<RuleAction>();

        void Translate()
        {
            // Get the reserved variables and IDs
            // foreach (ScriptFile script in Importer.ScriptFiles)
            // {
            //     if (script.Context.reserved_global()?.reserved_list() != null)
            //     {
            //         foreach (var name in script.Context.reserved_global().reserved_list().PART()) VarCollection.Reserve(name.GetText(), true);
            //         foreach (var id in script.Context.reserved_global().reserved_list().NUMBER()) VarCollection.Reserve(int.Parse(id.GetText()), true, null, null);
            //     }
            //     if (script.Context.reserved_player()?.reserved_list() != null)
            //     {
            //         foreach (var name in script.Context.reserved_player().reserved_list().PART()) VarCollection.Reserve(name.GetText(), false);
            //         foreach (var id in script.Context.reserved_player().reserved_list().NUMBER()) VarCollection.Reserve(int.Parse(id.GetText()), false, null, null);
            //     }
            // }

            // Get the enums
            foreach (ScriptFile script in Importer.ScriptFiles)
            foreach (var enumContext in script.Context.Enums)
            {
                var newEnum = new GenericCodeTypeInitializer(new DefinedEnum(new ParseInfo(script, this), enumContext));
                RulesetScope.AddType(newEnum);
                Types.AllTypes.Add(newEnum); 
                Types.DefinedTypes.Add(newEnum);
            }

            // Get the types
            foreach (ScriptFile script in Importer.ScriptFiles)
            foreach (var typeContext in script.Context.Classes)
            {
                var newType = new DefinedClassInitializer(new ParseInfo(script, this), GlobalScope, typeContext);
                RulesetScope.AddType(newType);
                Types.AllTypes.Add(newType);
                Types.DefinedTypes.Add(newType);
            }
            
            // Get the declarations
            foreach (ScriptFile script in Importer.ScriptFiles)
            {
                ParseInfo parseInfo = new ParseInfo(script, this);

                // Get the functions.
                foreach (var declaration in script.Context.Declarations)
                {
                    // Function
                    if (declaration is FunctionContext function)
                    {
                        var p = DefinedMethodProvider.GetDefinedMethod(parseInfo, this, function, null);
                        p.AddDefaultInstance(this);
                    }
                    // Macro function
                    else if (declaration is MacroFunctionContext macroFunction)
                        parseInfo.GetMacro(RulesetScope, RulesetScope, macroFunction);
                    // Macro var
                    else if (declaration is MacroVarDeclaration macroVar)
                        parseInfo.GetMacro(RulesetScope, RulesetScope, macroVar);
                    // Variables
                    else if (declaration is VariableDeclaration variable)
                    {
                        Var newVar = new RuleLevelVariable(RulesetScope, new DefineContextHandler(new ParseInfo(script, this), variable));
                        rulesetVariables.Add(newVar);

                        // Add the variable to the player variables scope if it is a player variable.
                        if (newVar.VariableType == VariableType.Player)
                            PlayerVariableScope.CopyVariable(newVar);
                    }
                }
            }

            foreach (var resolve in _resolveElements) resolve.ResolveElements();
            foreach (var apply in _applyBlocks) apply.SetupParameters();
            foreach (var apply in _applyBlocks) apply.SetupBlock();
            foreach (var callInfo in _recursionCheck) callInfo.CheckRecursion();

            // Get hooks
            foreach (ScriptFile script in Importer.ScriptFiles)
            foreach (var hookContext in script.Context.Hooks)
                HookVar.GetHook(new ParseInfo(script, this), RulesetScope, hookContext);

            // Get the rules
            foreach (ScriptFile script in Importer.ScriptFiles)
            foreach (var ruleContext in script.Context.Rules)
                rules.Add(new RuleAction(new ParseInfo(script, this), RulesetScope, ruleContext));
        }

        public string WorkshopCode { get; private set; }
        public int ElementCount { get; private set; }
        public List<Rule> WorkshopRules { get; private set; }

        void ToWorkshop(Func<VarCollection, Rule[]> addRules)
        {
            // Set up the variable collection.
            VarCollection.Setup();

            // Set up initial global and player rules.
            InitialGlobal = new TranslateRule(this, "Initial Global", RuleEvent.OngoingGlobal);
            InitialPlayer = new TranslateRule(this, "Initial Player", RuleEvent.OngoingPlayer);
            WorkshopRules = new List<Rule>();

            // Init called types.
            foreach (var workshopInit in _workshopInit) workshopInit.WorkshopInit(this);

             // Assign variables at the rule-set level.
            foreach (var variable in rulesetVariables)
            {
                // Assign the variable an index.
                var assigner = DefaultIndexAssigner.Add(VarCollection, variable, true, null) as IndexReference;

                // Assigner will be non-null if it is an IndexReference.
                if (assigner != null)
                {
                    DebugVariables.Add(variable, assigner);
                    // Initial value.
                    if (variable.InitialValue != null)
                    {
                        var addToInitialRule = GetInitialRule(variable.VariableType == VariableType.Global);

                        addToInitialRule.ActionSet.AddAction(assigner.SetVariable(
                            (Element)variable.InitialValue.Parse(addToInitialRule.ActionSet)
                        ));
                    }
                }
            }

            // Parse the rules.
            foreach (var rule in rules)
            {
                var translate = new TranslateRule(this, rule);
                Rule newRule = translate.GetRule();
                WorkshopRules.Add(newRule);
                rule.ElementCountLens.RuleParsed(newRule);
            }

            // Add built-in rules.
            // Initial player
            if (InitialPlayer.Actions.Count > 0)
                WorkshopRules.Insert(0, InitialPlayer.GetRule());

            // Initial global
            if (InitialGlobal.Actions.Count > 0)
                WorkshopRules.Insert(0, InitialGlobal.GetRule());
            
            // Additional
            if (addRules != null)
                WorkshopRules.AddRange(addRules.Invoke(VarCollection).Where(rule => rule != null));
                        
            // Order the workshop rules by priority.
            WorkshopRules = WorkshopRules.OrderBy(wr => wr.Priority).ToList();

            // Get the final workshop string.
            WorkshopBuilder result = new WorkshopBuilder(Language);
            LanguageInfo.I18nWarningMessage(result, Language);

            // Get the custom game settings.
            if (Importer.MergedLobbySettings != null)
            {
                Ruleset settings = Ruleset.Parse(Importer.MergedLobbySettings);
                settings.ToWorkshop(result);
                result.AppendLine();
            }

            // Get the variables.
            VarCollection.ToWorkshop(result);
            result.AppendLine();

            // Print class identifiers.
            // Types.PrintClassIdentifiers(result);

            // Get the subroutines.
            SubroutineCollection.ToWorkshop(result);

            // Get the rules.
            for (int i = 0; i < WorkshopRules.Count; i++)
            {
                WorkshopRules[i].ToWorkshop(result, OptimizeOutput);
                ElementCount += WorkshopRules[i].ElementCount(OptimizeOutput);
                if (i != WorkshopRules.Count - 1) result.AppendLine();
            }
            
            WorkshopCode = result.GetResult();
        }

        public ScriptFile ScriptFromUri(Uri uri) => Importer.ScriptFiles.FirstOrDefault(script => script.Uri.Compare(uri));

        private TranslateRule GetInitialRule(bool isGlobal)
        {
            return isGlobal ? InitialGlobal : InitialPlayer;
        }

        // Applyable blocks
        private readonly List<IApplyBlock> _applyBlocks = new List<IApplyBlock>();
        private readonly List<CallInfo> _recursionCheck = new List<CallInfo>();
        public void ApplyBlock(IApplyBlock apply)
        {
            _applyBlocks.Add(apply);
            if (apply.CallInfo != null) _recursionCheck.Add(apply.CallInfo);
        }
        public void RecursionCheck(CallInfo callInfo)
        {
            _recursionCheck.Add(callInfo ?? throw new ArgumentNullException(nameof(callInfo)));
        }

        // Element resolve
        private readonly List<IResolveElements> _resolveElements = new List<IResolveElements>();
        public void AddResolve(IResolveElements resolveElement)
        {
            if (!_resolveElements.Contains(resolveElement))
                _resolveElements.Add(resolveElement);
        }

        // Workshop init
        private readonly List<IWorkshopInit> _workshopInit = new List<IWorkshopInit>();
        public void AddWorkshopInit(IWorkshopInit workshopInit)
        {
            if (!_workshopInit.Contains(workshopInit))
                _workshopInit.Add(workshopInit);
        }

        Scope IScopeProvider.GetObjectBasedScope() => RulesetScope;
        Scope IScopeProvider.GetStaticBasedScope() => RulesetScope;
        void IScopeAppender.AddObjectBasedScope(IMethod function) => RulesetScope.CopyMethod(function);
        void IScopeAppender.AddStaticBasedScope(IMethod function) => RulesetScope.CopyMethod(function);
        IMethod IScopeProvider.GetOverridenFunction(IMethodProvider provider) => throw new NotImplementedException();
    }

    public class ScriptTypes : ITypeSupplier
    {
        private readonly DeltinScript _deltinScript;
        public List<ICodeTypeInitializer> AllTypes { get; } = new List<ICodeTypeInitializer>();
        public List<ICodeTypeInitializer> DefinedTypes { get; } = new List<ICodeTypeInitializer>();
        private readonly PlayerType _playerType;
        private readonly VectorType _vectorType;
        private readonly NumberType _numberType;
        private readonly StringType _stringType;
        private readonly BooleanType _booleanType;

        public ScriptTypes(DeltinScript deltinScript)
        {
            _deltinScript = deltinScript;
            _playerType = new PlayerType(this);
            _vectorType = new VectorType(this);
            _numberType = new NumberType(this);
            _stringType = new StringType(this);
            _booleanType = new BooleanType(this);
        }

        public void GetDefaults()
        {
            var dynamicType = new AnyType(_deltinScript);
            AddType(dynamicType);
            AddType(_playerType);
            AddType(_vectorType);
            AddType(_numberType);
            AddType(_stringType);
            AddType(_booleanType);
            AddType(Positionable.Instance);
            AddType(Pathfinder.SegmentsStruct.Instance);
            // Pathfinder classes
            AddType(new Pathfinder.PathmapClass(_deltinScript));
            AddType(new Pathfinder.PathResolveClass(this));
            // Constant lambda types.
            AddType(new Lambda.BlockLambda(dynamicType));
            AddType(new Lambda.ValueBlockLambda(dynamicType));
            AddType(new Lambda.MacroLambda(dynamicType));
            // Enums
            foreach (var type in ValueGroupType.GetEnumTypes(this))
                AddType(type);

            foreach (var type in AllTypes)
                if (type is IResolveElements resolveElements)
                    resolveElements.ResolveElements();
                else if (type.GetInstance() is IResolveElements resolveDefaultInstance)
                    resolveDefaultInstance.ResolveElements();

            _deltinScript.PlayerVariableScope = _playerType.PlayerVariableScope;
        }

        private void AddType(CodeType type) => AllTypes.Add(new GenericCodeTypeInitializer(type));
        private void AddType(ICodeTypeInitializer initializer) => AllTypes.Add(initializer);

        public void AddTypesToScope(Scope scope)
        {
            foreach (var type in AllTypes)
                scope.AddType(type);
        }

        public void PrintClassIdentifiers(WorkshopBuilder builder)
        {
            builder.AppendLine("// Class identifiers:");

            foreach (CodeType type in AllTypes)
                if (type is ClassType classType && classType.Identifier > 0)
                    builder.AppendLine("// " + classType.Name + ": " + classType.Identifier);
            
            builder.AppendLine();
        }

        public T GetInstance<T>() where T: CodeType => (T)AllTypes.First(type => type.BuiltInTypeMatches(typeof(T))).GetInstance();
        public CodeType GetInstanceFromInitializer<T>() where T: ICodeTypeInitializer => AllTypes.First(type => type.GetType() == typeof(T)).GetInstance();
        public T GetInitializer<T>() where T: ICodeTypeInitializer => (T)AllTypes.First(type => type.GetType() == typeof(T));

        public CodeType Default() => Any();
        public CodeType Any() => GetInstance<AnyType>();
        public CodeType AnyArray() => new ArrayType(this, Any());
        public CodeType Boolean() => GetInstance<BooleanType>();
        public CodeType Number() => GetInstance<NumberType>();
        public CodeType String() => GetInstance<StringType>();
        public CodeType Player() => _playerType;
        public CodeType Players() => new PipeType(_playerType, PlayerArray());
        public CodeType PlayerArray() => new ArrayType(this, _playerType);
        public CodeType Vector() => _vectorType;
        public CodeType VectorArray() => new ArrayType(this, _vectorType);
        public CodeType PlayerOrVector() => new PipeType(Player(), Vector());
        public CodeType Button() => Any(); // TODO

        public CodeType EnumType(string typeName)
        {
            foreach (var type in AllTypes)
                if (type is ValueGroupType valueGroupType && type.Name == typeName)
                    return type;
            throw new Exception("No enum type by the name of '" + typeName + "' exists.");
        }
    }

    public interface IComponent
    {
        DeltinScript DeltinScript { get; set; }
        void Init();
    }

    class InitComponent
    {
        public Type ComponentType { get; }
        public Action<IComponent> Apply { get; }

        public InitComponent(Type componentType, Action<IComponent> apply)
        {
            ComponentType = componentType;
            Apply = apply;
        }
    }
}