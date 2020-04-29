#load "c:/Development/crazy/.paket/load/netStandard2.0/Transpiler/FSharp.Compiler.Service.fsx"
#I @"C:\development\Fable\src\Fable.Transforms"
#load @"Global\Fable.Core.fs" @"Global\Prelude.fs" @"Global\Compiler.fs" @"AST\AST.Common.fs" @"AST\AST.Fable.fs" @"MonadicTrampoline.fs" @"Transforms.Util.fs" @"OverloadSuffix.fs" @"FSharp2Fable.Util.fs" @"ReplacementsInject.fs" @"Replacements.fs" @"Inject.fs" @"FSharp2Fable.fs" @"FableTransforms.fs"
open System
open Fable
open FSharp.Compiler.SourceCodeServices
open System.Collections.Generic
open System.Collections.Concurrent
open System.IO

type Project(projectOptions: FSharpProjectOptions,
                implFiles: IDictionary<string, FSharpImplementationFileContents>,
                errors: FSharpErrorInfo array) =
    let projectFile = Path.normalizePath projectOptions.ProjectFileName
    let inlineExprs = ConcurrentDictionary<string, InlineExpr>()
    //let rootModules =
    //    implFiles |> Seq.map (fun kv ->
    //        kv.Key, FSharp2Fable.Compiler.getRootModuleFullName kv.Value) |> dict
    member __.ImplementationFiles = implFiles
    member __.RootModules = dict [] //rootModules
    member __.InlineExprs = inlineExprs
    member __.Errors = errors
    member __.ProjectOptions = projectOptions
    member __.ProjectFile = projectFile
    member __.GetOrAddInlineExpr(fullName, generate) =
        inlineExprs.GetOrAdd(fullName, fun _ -> generate())


type Log =
    { Message: string
      Tag: string
      Severity: Severity
      Range: SourceLocation option
      FileName: string option }

type Compiler(currentFile, project: Project, options, fableLibraryDir: string) =
    let mutable id = 0
    let logs = ResizeArray<Log>()
    let fableLibraryDir = fableLibraryDir.TrimEnd('/')
    member __.GetLogs() =
        logs |> Seq.toList
    member __.GetFormattedLogs() =
        let severityToString = function
            | Severity.Warning -> "warning"
            | Severity.Error -> "error"
            | Severity.Info -> "info"
        logs
        |> Seq.groupBy (fun log -> severityToString log.Severity)
        |> Seq.map (fun (severity, logs) ->
            logs |> Seq.map (fun log ->
                match log.FileName with
                | Some file ->
                    match log.Range with
                    | Some r -> sprintf "%s(%i,%i): (%i,%i) %s %s: %s" file r.start.line r.start.column r.``end``.line r.``end``.column severity log.Tag log.Message
                    | None -> sprintf "%s(1,1): %s %s: %s" file severity log.Tag log.Message
                | None -> log.Message)
            |> Seq.toArray
            |> Tuple.make2 severity)
        |> Map
    member __.Options = options
    member __.CurrentFile = currentFile
    interface ICompiler with
        member __.Options = options
        member __.LibraryDir = fableLibraryDir
        member __.CurrentFile = currentFile
        member x.GetRootModule(fileName) =
            let fileName = Path.normalizePathAndEnsureFsExtension fileName
            match project.RootModules.TryGetValue(fileName) with
            | true, rootModule -> rootModule
            | false, _ ->
                let msg = sprintf "Cannot find root module for %s. If this belongs to a package, make sure it includes the source files." fileName
                (x :> ICompiler).AddLog(msg, Severity.Warning)
                "" // failwith msg
        member __.GetOrAddInlineExpr(fullName, generate) =
            project.InlineExprs.GetOrAdd(fullName, fun _ -> generate())
        member __.AddLog(msg, severity, ?range, ?fileName:string, ?tag: string) =
            { Message = msg
              Tag = defaultArg tag "FABLE"
              Severity = severity
              Range = range
              FileName = fileName }
            |> logs.Add
        // TODO: If name includes `$$2` at the end, remove it
        member __.GetUniqueVar(name) =
            id <- id + 1
            Naming.getUniqueName (defaultArg name "var") id



type PhpConst =
    | PhpConstNumber of float
    | PhpConstString of string
    | PhpConstBool of bool
    | PhpConstNull
    | PhpConstUnit

type PhpArrayIndex =
    | PhpArrayInt of int
    | PhpArrayString of string
type PhpField =
    { Name: string 
      Type: string }

and PhpExpr =
    | PhpVar of string * typ: PhpType option
    | PhpGlobal of string
    | PhpConst of PhpConst
    | PhpUnaryOp of string * PhpExpr
    | PhpBinaryOp of string *PhpExpr * PhpExpr
    | PhpProp of PhpExpr * PhpField
    | PhpArrayAccess of PhpExpr * PhpExpr
    | PhpNew of ty:PhpType * args:PhpExpr list
    | PhpArray of args: PhpExpr list
    | PhpArrayMap of (PhpArrayIndex * PhpExpr) list
    | PhpCall of func:string * args: PhpExpr list
    | PhpMethod of this: PhpExpr * func:string * args: PhpExpr list
    | PhpTernary of gard: PhpExpr * thenExpr: PhpExpr * elseExpr: PhpExpr
    | PhpIsA of expr: PhpExpr * PhpType
    | PhpAnonymousFunc of args: string list * uses: string list * body: PhpExpr
   
and PhpStatement =
    | Return of PhpExpr
    | Expr of PhpExpr
    | Switch of PhpExpr * (PhpCase * PhpStatement list) list
    | Break
    | Assign of name:string * PhpExpr
    | If of guard: PhpExpr * thenCase: PhpStatement list * elseCase: PhpStatement list
and PhpCase =
    | IntCase of int
    | StringCase of string
    | DefaultCase

and PhpFun = 
    { Name: string
      Args: string list
      Matchings: PhpStatement list
      Body: PhpStatement list
      Static: bool
    }

and PhpType =
    { Name: string
      Fields: PhpField list;
      Methods: PhpFun list
      Abstract: bool
      BaseType: PhpType option
    }


type PhpDecl =
    | PhpFun of PhpFun
    | PhpDeclValue of name:string * PhpExpr
    | PhpType of PhpType

type PhpFile =
    { Decls: PhpDecl list }


module Output =
    type Writer =
        { Writer: TextWriter
          Indent: int
          Precedence: int }

    let indent ctx =
        { ctx with Indent = ctx.Indent + 1}

    module Writer =
        let create w =
            { Writer = w; Indent = 0; Precedence = 0 }

    let writeIndent  ctx =
        for _ in 1 .. ctx.Indent do
            ctx.Writer.Write("    ")

    let write ctx txt =
        ctx.Writer.Write(txt: string)


    let writeln ctx txt =
         ctx.Writer.WriteLine(txt: string)

    let writei ctx txt =
        writeIndent ctx
        write ctx txt

    let writeiln ctx txt =
        writeIndent ctx
        writeln ctx txt
      
    let writeVarList ctx vars =
        let mutable first = true
        for var in vars do
            if first then
                first <- false
            else
                write ctx ", "
            write ctx "$"
            write ctx var

    let opPrecedence =
        function
        | "+" -> 0
        | "-" -> 0
        | "*" -> 1
        | "/" -> 1
        | "%" -> 1
        | _   -> 2

    let rec writeExpr ctx expr =
        match expr with
        | PhpBinaryOp(op, left, right) ->
            let opPrec = opPrecedence op
            let subCtx = { ctx with Precedence = opPrec }
            if opPrec < ctx.Precedence then
                write subCtx "("

            writeExpr subCtx left
            write subCtx " "
            write subCtx op
            write subCtx " "
            writeExpr subCtx right

            if opPrec < ctx.Precedence then
                write subCtx ")"
        | PhpUnaryOp(op, expr) ->
            write ctx op
            writeExpr ctx expr
        | PhpConst cst -> 
            match cst with
            | PhpConstNumber n -> write ctx (string n)
            | PhpConstString s -> write ctx s
            | PhpConstBool true -> write ctx "true"
            | PhpConstBool false -> write ctx "false"
            | PhpConstNull -> write ctx "NULL"
            | PhpConstUnit -> ()
        | PhpVar (v,_) -> 
            write ctx "$"
            write ctx v
        | PhpGlobal v -> 
            write ctx "$GLOBALS['"
            write ctx v
            write ctx "']"
        | PhpProp(l,r) ->
            writeExpr ctx l
            write ctx "->"
            write ctx r.Name
        | PhpNew(t,args) ->
            write ctx "new "
            write ctx t.Name
            write ctx "("
            writeArgs ctx args
            write ctx ")"
        | PhpArray(args) ->
            write ctx "[ "
            writeArgs ctx args
            write ctx " ]"
        | PhpArrayMap(args) ->
            write ctx "[ "
            let mutable first = true
            for key,value in args do
                if first then
                    first <- false
                else
                    write ctx ", "
                writeArrayIndex ctx key
                write ctx " => "
                writeExpr ctx value
            write ctx "]"
        | PhpArrayAccess(array, index) ->
            writeExpr ctx array
            write ctx "["
            writeExpr ctx index
            write ctx "]"

        | PhpCall(f,args) ->
            write ctx f
            write ctx "("
            writeArgs ctx args
            write ctx ")"
        | PhpMethod(this,f,args) ->
            writeExpr ctx this
            write ctx "->"
            write ctx f
            write ctx "("
            writeArgs ctx args
            write ctx ")"
        | PhpTernary (guard, thenExpr, elseExpr) ->
            writeExpr ctx guard
            write ctx " ? "
            writeExpr ctx thenExpr
            write ctx " : "
            writeExpr ctx elseExpr
        | PhpIsA (expr, t) ->
            writeExpr ctx expr
            write ctx " instanceof "
            write ctx t.Name
        | PhpAnonymousFunc(args, uses, body) ->
            write ctx "function ("
            writeVarList ctx args
            write ctx ")"
            match uses with
            | [] -> ()
            | _ ->
                write ctx " use ("
                writeVarList ctx uses
                write ctx ")"
            write ctx " { return "
            writeExpr ctx body
            write ctx "; }"
    and writeArgs ctx args =
        let mutable first = true
        for arg in args do
            if first then
                first <- false
            else
                write ctx ", "
            writeExpr ctx arg
    and writeArrayIndex ctx index =
        match index with
        | PhpArrayString s  ->
            write ctx "'"
            write ctx s
            write ctx "'"
        | PhpArrayInt n  ->
            write ctx (string n)

        
    let rec writeStatement ctx st =
        match st with
        | Return expr ->
            writei ctx "return "
            writeExpr ctx expr
            writeln ctx ";"
        | Expr expr ->
            writei ctx ""
            writeExpr ctx expr
            writeln ctx ";"
        | Assign(name, expr) ->
            writei ctx "$"
            write ctx name
            write ctx " = "
            writeExpr ctx expr
            writeln ctx ";"
        | Switch(expr, cases) ->
            writei ctx "switch ("
            writeExpr ctx expr
            writeln ctx ")"
            writeiln ctx "{"
            let casesCtx = indent ctx
            let caseCtx = indent casesCtx
            for case,sts in cases do
                match case with
                | IntCase i -> 
                    writei casesCtx "case "
                    write casesCtx (string i)
                | StringCase s -> 
                    writei casesCtx "case '"
                    write casesCtx s
                    write casesCtx "'"
                | DefaultCase ->
                    writei casesCtx "default"
                writeln casesCtx ":"
                for st in sts do
                    writeStatement caseCtx st

            writeiln ctx "}"
        | Break ->
            writei ctx "break;"
        | If(guard, thenCase, elseCase) ->
            writei ctx "if ("
            writeExpr ctx guard
            writeln ctx ") {"
            let body = indent ctx
            for st in thenCase do
                writeStatement body st
            writeiln ctx "} else {"
            for st in elseCase do
                writeStatement body st
            writeiln ctx "}"


    let writeFunc ctx (f: PhpFun) =
        writei ctx ""
        if f.Static then
            write ctx "static "
        
        write ctx "function "
        write ctx f.Name
        write ctx "("
        let mutable first = true
        for arg in f.Args do
            if first then
                first <- false
            else
                write ctx ", "
            write ctx "$"
            write ctx arg
        writeln ctx ") {"
        let bodyCtx = indent ctx
        for s in f.Matchings do
            writeStatement bodyCtx s

        for s in f.Body do
            writeStatement bodyCtx s
        writeiln ctx "}"
            
    let writeField ctx (m: PhpField) =
        writei ctx "public $"
        write ctx m.Name
        writeln ctx ";"

    let writeCtor ctx (t: PhpType) =
        writei ctx "function __construct("
        let mutable first = true
        for p in t.Fields do
            if first then
                first <- false
            else
                write ctx ", "
            //write ctx p.Type
            write ctx "$"
            write ctx p.Name
        writeln ctx ") {"
        let bodyctx = indent ctx
        for p in t.Fields do
            writei bodyctx "$this->"
            write bodyctx p.Name
            write bodyctx " = $"
            write bodyctx p.Name
            writeln bodyctx ";"

        writeiln ctx "}"

    let writeType ctx (t: PhpType) =
        writei ctx ""
        if t.Abstract then
            write ctx "abstract "
        write ctx "class "
        write ctx t.Name
        match t.BaseType with
        | Some t ->
            write ctx " extends "
            write ctx t.Name
        | None -> ()

        writeln ctx " {" 
        let mbctx = indent ctx
        for m in t.Fields do
            writeField mbctx m

        if not t.Abstract then
            writeCtor mbctx t

        for m in t.Methods do
            writeFunc mbctx m

        writeiln ctx "}"


    let writeAssign ctx n expr =
        writei ctx "$"
        write ctx n
        write ctx " = "
        writeExpr ctx expr
        writeln ctx ";"


    let writeDecl ctx d =
        match d with
        | PhpType t -> writeType ctx t
        | PhpFun t -> writeFunc ctx t
        | PhpDeclValue(n,expr) -> writeAssign ctx n expr

    let writeFile ctx (file: PhpFile) =
        writeln ctx "<?php"
        for d in file.Decls do
            writeDecl ctx d
            writeln ctx ""





open Fable.AST

module PhpList =
    let list  = { Name = "List"; Fields = []; Methods = []; Abstract = true; BaseType = None }
    let cons = { Name = "Cons"; Fields = []; Methods = []; Abstract = false; BaseType = Some list } 
    let nil = { Name = "Nil"; Fields = []; Methods = []; Abstract = false; BaseType = Some list }

type PhpCompiler =
    { mutable Types: Map<string,PhpType> 
      DecisionTargets: (Fable.Ident list * Fable.Expr) list
      mutable LocalVars: string Set
      mutable CapturedVars: string Set
    }
    static member empty =

        { Types = Map.ofList [ "List" , PhpList.list
                               "Cons" , PhpList.cons
                               "Nil", PhpList.nil ]  
          DecisionTargets = []
          LocalVars = Set.empty
          CapturedVars = Set.empty
          }
    member this.AddType(phpType: PhpType) =
        this.Types <- Map.add phpType.Name phpType this.Types
        phpType

    member this.AddLocalVar(var) =
        this.LocalVars <- Set.add var this.LocalVars

    member this.UseVar(var) =
        if not (Set.contains var this.LocalVars) then
            this.CapturedVars <- Set.add var this.CapturedVars

    member this.NewScope() =
        { this with 
            LocalVars = Set.empty
            CapturedVars = Set.empty }


let convertType (t: FSharpType) =
    if (t.IsAbbreviation) then
        t.Format(FSharpDisplayContext.Empty.WithShortTypeNames(true))
    else
        match t with
        | Symbol.TypeWithDefinition entity ->
            match entity.CompiledName with
            | "FSharpSet`1" -> "Set"
            | name -> name
        | _ ->
            failwithf "%A" t
       

let fixName (name: string) =
    name.Replace('$','_')

let caseName (case: FSharpUnionCase) =
    let entity = case.ReturnType.TypeDefinition
    if entity.UnionCases.Count = 1 then
        case.Name
    else
        entity.CompiledName + "_" + case.Name


let convertUnion (ctx: PhpCompiler) (info: Fable.UnionConstructorInfo) = 
    if info.Entity.UnionCases.Count = 1 then
        let case = info.Entity.UnionCases.[0] 
        [ let t =
            { Name = case.Name
              Fields = [ for e in case.UnionCaseFields do 
                            { Name = e.Name 
                              Type  = convertType e.FieldType } ]
              Methods = [ ]
              Abstract = false
              BaseType = None}
          ctx.AddType(t) |> PhpType ]
    else
    [ let baseType =
            { Name = info.Entity.CompiledName
              Fields = []
              Methods = []
              Abstract = true 
              BaseType = None }
      ctx.AddType(baseType) |> PhpType

      for case in info.Entity.UnionCases do
        let t = 
            { Name = caseName case
              Fields = [ for e in case.UnionCaseFields do 
                            { Name = e.Name 
                              Type  = convertType e.FieldType } ]
              Methods = [ ]
              Abstract = false
              BaseType = Some baseType }
        ctx.AddType(t) |> PhpType ]


let rec convertExpr (ctx: PhpCompiler) (expr: Fable.Expr) =
    match expr with
    | Fable.Value(value,_) ->
        convertValue ctx value

    | Fable.Operation(Fable.BinaryOperation(op,left,right),t,_) ->
        let opstr =
            match op with
            | BinaryOperator.BinaryMultiply -> "*"
            | BinaryOperator.BinaryPlus -> "+"
            | BinaryOperator.BinaryMinus -> "-"
            | BinaryOperator.BinaryLess -> "<"
            | BinaryOperator.BinaryGreater -> ">"
            | BinaryOperator.BinaryLessOrEqual -> "<="
            | BinaryOperator.BinaryGreaterOrEqual -> ">="
            | BinaryOperator.BinaryAndBitwise -> "&"
            | BinaryOperator.BinaryOrBitwise -> "|"
            | BinaryOperator.BinaryEqual -> "=="
            | BinaryOperator.BinaryEqualStrict -> "==="
            | BinaryOperator.BinaryUnequalStrict -> "!=="
            | BinaryOperator.BinaryModulus -> "%"
        PhpBinaryOp(opstr, convertExpr ctx left, convertExpr ctx right)
    | Fable.Operation(Fable.UnaryOperation(op, expr),_,_) ->
        let opStr = 
            match op with
            | UnaryOperator.UnaryNot -> "!"
            | UnaryOperator.UnaryMinus -> "-"
            | UnaryOperator.UnaryPlus -> "+"

        PhpUnaryOp(opStr, convertExpr ctx expr)

    | Fable.Operation(Fable.Call(Fable.StaticCall(Fable.Import(Fable.Value(Fable.StringConstant s, _ ) ,p,k,ty,_)), args),t,_) ->
        match k,p with
        | Fable.ImportKind.Library, Fable.Value(Fable.StringConstant cls,_) ->
            match s with
            | "op_UnaryNegation_Int32" -> PhpUnaryOp("-", convertExpr ctx args.Args.[0])
            | _ -> 
                let phpCls =
                    match cls with
                    | "List" -> "FSharpList"
                    | _ -> cls
                PhpCall(phpCls + "::" + fixName s, [ for arg in args.Args do convertExpr ctx arg])
        | _ -> PhpCall(fixName s, [ for arg in args.Args do convertExpr ctx arg])
    | Fable.Operation(Fable.Call(Fable.InstanceCall( Some (Fable.Value(Fable.StringConstant s, _ ))),{ Args = args; ThisArg = Some this}), _, _) ->
        PhpMethod(convertExpr ctx this,fixName s, [for arg in args -> convertExpr ctx arg ] )

    | Fable.Get(expr,Fable.UnionField(f,case,_),t,_) ->
        let name = caseName case
            
        let t = Map.find name ctx.Types
        let field = t.Fields |> List.find (fun ff -> ff.Name = f.Name)
        PhpProp(convertExpr ctx expr, field)
    | Fable.Get(expr,Fable.OptionValue,_,_) ->
        convertExpr ctx expr    
    | Fable.Get(expr,Fable.FieldGet(fieldName,_,_),_ ,_) ->
        
        match convertExpr ctx expr with
        | PhpVar(_, Some phpType) as v -> 
            let field = phpType.Fields |> List.find (fun f -> f.Name = fieldName)
        
            PhpProp(v,field)

    | Fable.Get(expr,Fable.GetKind.TupleGet(id),_,_) ->
        PhpArrayAccess(convertExpr ctx expr, PhpConst(PhpConstNumber (float id))) 
    | Fable.Get(expr, Fable.ExprGet(expr'),_,_) ->
        let prop = convertExpr ctx expr'
        PhpArrayAccess(convertExpr ctx expr, prop)


    | Fable.IdentExpr(id) ->
        let name = fixName id.Name
        ctx.UseVar(name)
        let phpType = 
            match id.Type with
            | Fable.Type.DeclaredType(e,_) ->
                Map.tryFind e.CompiledName ctx.Types

            | _ -> None 
        
        PhpVar(name, phpType)
    | Fable.Import(expr,_,_,_,_) ->
        match convertExpr ctx expr with
        | PhpConst (PhpConstString s) -> PhpGlobal (fixName s)
        | exp -> exp

    | Fable.DecisionTree(expr,targets) ->
        convertExpr { ctx with DecisionTargets = targets } expr
    | Fable.IfThenElse(guard, thenExpr, elseExpr,_) ->
        PhpTernary(convertExpr ctx guard,
                convertExpr ctx thenExpr,
                convertExpr ctx elseExpr )
    | Fable.Test(expr, Fable.TestKind.UnionCaseTest(case,_), _ ) ->
        let t = Map.find (caseName case) ctx.Types
        PhpIsA(convertExpr ctx expr, t)
    | Fable.Test(expr, Fable.TestKind.ListTest(isCons),_) ->
        PhpIsA(convertExpr ctx expr, if isCons then PhpList.cons else PhpList.nil)
    | Fable.Test(expr, Fable.OptionTest(isSome), _) ->
        let isNull = PhpCall("is_null", [convertExpr ctx expr])
        if isSome then
            PhpUnaryOp("!",isNull)
        else
            isNull
        
    | Fable.DecisionTreeSuccess(index,_,_) ->
        let _,target = ctx.DecisionTargets.[index]
        convertExpr ctx target

    | Fable.ObjectExpr(members, t, baseCall) ->
         PhpArrayMap [
            for m in members do
                match m with
                | Fable.ObjectMember(Fable.Value(Fable.StringConstant key,_) ,value,kind) ->
                    PhpArrayString key , convertExpr ctx value
         ]
    | Fable.Function(Fable.Lambda(arg),body,_) ->
        let scope = ctx.NewScope()
        let argName = fixName arg.Name
        scope.AddLocalVar argName
        let phpBody = convertExpr scope body

        PhpAnonymousFunc([argName], Set.toList scope.CapturedVars , phpBody )
    | Fable.Let([], body) ->
        convertExpr ctx body
    | Fable.Expr.TypeCast(expr, t) ->
        convertExpr ctx expr
        
        
        

and convertValue (ctx:PhpCompiler) (value: Fable.ValueKind) =
    match value with
    | Fable.NewUnion(args,case,_,_) ->
        let t = Map.find (caseName case) ctx.Types
        PhpNew(t, [for arg in args do convertExpr ctx arg ])
    | Fable.NewTuple(args) ->
        
        PhpArray([for arg in args do convertExpr ctx arg])
    | Fable.NewRecord(args, Fable.DeclaredRecord(e), _) ->
        let t = ctx.Types.[e.CompiledName]
        PhpNew(t, [ for arg in args do convertExpr ctx arg ] )
        

    | Fable.ValueKind.NumberConstant(v,_) ->
        PhpConst(PhpConstNumber v)
    | Fable.ValueKind.StringConstant(s) ->
        PhpConst(PhpConstString s)
    | Fable.ValueKind.BoolConstant(b) ->
        PhpConst(PhpConstBool b)
    | Fable.ValueKind.UnitConstant ->
        PhpConst(PhpConstUnit)
    | Fable.ValueKind.Null _ ->
        PhpConst(PhpConstNull)

let convertRecord (ctx: PhpCompiler) (info: Fable.CompilerGeneratedConstructorInfo) = 
    [ let t =
        { Name = info.Entity.CompiledName
          Fields = [ for e in info.Entity.FSharpFields do 
                        { Name = e.Name 
                          Type  = convertType e.FieldType } ]
          Methods = [ ]
          Abstract = false
          BaseType = None}
      ctx.AddType(t) |> PhpType ]

type ReturnStrategy =
    | Return
    | ReturnUnit


let rec canBeCompiledAsSwitch tree =
    match tree with
    | Fable.IfThenElse(Fable.Test(_, Fable.UnionCaseTest(case,e),_), Fable.DecisionTreeSuccess(index,_,_), elseExpr,_) ->
        canBeCompiledAsSwitch elseExpr
    | Fable.DecisionTreeSuccess(index, _,_) ->
        true
    | _ -> false

let rec findCasesNames tree =
    [ match tree with
      | Fable.IfThenElse(Fable.Test(_, Fable.UnionCaseTest(case,e),_), Fable.DecisionTreeSuccess(index,_,_), elseExpr,_) ->
            Some case,index
            yield! findCasesNames elseExpr
      | Fable.DecisionTreeSuccess(index, _,_) ->
            None, index
    ]

    

let rec convertExprToStatement ctx expr returnStrategy =
    match expr with
    | Fable.DecisionTree(input, targets) ->
        convertExprToStatement { ctx with DecisionTargets = targets} input returnStrategy
    | Fable.IfThenElse(Fable.Test(expr, Fable.TestKind.UnionCaseTest(case,entity), _) as guard, thenExpr , elseExpr, _) as input ->
        if (canBeCompiledAsSwitch input) then
            let cases = findCasesNames input
            [ Switch(PhpCall("get_class", [convertExpr ctx expr]),
                [ for case,i in cases ->
                    let _,target = ctx.DecisionTargets.[i]
                    let phpCase =
                        match case with
                        | Some c -> StringCase c.Name
                        | None -> DefaultCase
                    phpCase, convertExprToStatement ctx target returnStrategy ] 
                )
            
            ]
        else
            [ If(convertExpr ctx guard, convertExprToStatement ctx thenExpr returnStrategy, convertExprToStatement ctx elseExpr returnStrategy) ]
    | Fable.IfThenElse(Fable.Test(expr, Fable.TestKind.OptionTest(isSome), _), thenExpr, elseExpr, _) ->
        let isNull = PhpCall("is_null", [convertExpr ctx expr])
        let guard = 
            if isSome then
                PhpUnaryOp("!",isNull)
            else
                isNull

        [ If(guard, convertExprToStatement ctx thenExpr returnStrategy,
                    convertExprToStatement ctx elseExpr returnStrategy) ]
    | Fable.DecisionTreeSuccess(index,_,_) ->
        let _,target = ctx.DecisionTargets.[index]
        convertExprToStatement ctx target returnStrategy
    | Fable.Let([ident, expr],body) ->
        [ Assign(fixName ident.Name, convertExpr ctx expr)
          yield! convertExprToStatement ctx body returnStrategy ]
    | Fable.Let([],body) ->
        convertExprToStatement ctx body returnStrategy


    | _ ->
        [  PhpStatement.Return (convertExpr ctx expr) ]

let convertDecl ctx decl =
    match decl with
    | Fable.Declaration.ConstructorDeclaration(Fable.UnionConstructor(info),_) -> 
        convertUnion ctx info
    | Fable.Declaration.ConstructorDeclaration(Fable.CompilerGeneratedConstructor(info),_) -> 
        convertRecord ctx info
    | Fable.Declaration.ValueDeclaration(Fable.Function(Fable.FunctionKind.Delegate(args), body, Some name),decl) ->
       [{ PhpFun.Name = fixName name
          Args = [ for arg in args do 
                    fixName arg.Name ]
          Matchings = []
          Body = convertExprToStatement ctx body Return 
          Static = false } |> PhpFun ]
    | Fable.Declaration.ValueDeclaration(expr , decl) ->
        [ PhpDeclValue(fixName decl.Name, convertExpr ctx expr) ]
    | _ -> [] 

let opts   =
    let projOptions: FSharpProjectOptions =
             {
                 ProjectId = None
                 ProjectFileName = @"C:\development\crazy\src\Game\Game.fsproj"
                 SourceFiles = [| @"C:\development\crazy\src\Shared\Shared.fs"
                                  @"C:\development\crazy\src\Shared\SharedGame.fs"|]
                 OtherOptions = [||]
                 ReferencedProjects = [||] //p2pProjects |> Array.ofList
                 IsIncompleteTypeCheckEnvironment = false
                 UseScriptResolutionRules = false
                 LoadTime = DateTime.Now
                 UnresolvedReferences = None;
                 OriginalLoadReferences = []
                 ExtraProjectInfo = None
                 Stamp = None
             }
    projOptions


let checker = FSharpChecker.Create(keepAssemblyContents = true)
let result = checker.ParseAndCheckProject(opts) |> Async.RunSynchronously
let impls =
    [ for imp in result.AssemblyContents.ImplementationFiles do 
        imp.FileName, imp
        ]
    |> dict

let proj = Project(opts ,impls,[||])
let compOptions =
    { CompilerOptions.typedArrays = false
      CompilerOptions.clampByteArrays = false
      CompilerOptions.debugMode = false
      CompilerOptions.outputPublicInlinedFunctions = false
      CompilerOptions.precompiledLib = None
      CompilerOptions.verbosity = Verbosity.Normal}
let com = Compiler(@"C:\development\crazy\src\Shared\Shared.fs", proj, compOptions, "")
let ast = 
    Fable.Transforms.FSharp2Fable.Compiler.transformFile com proj.ImplementationFiles
    |> Fable.Transforms.FableTransforms.optimizeFile com
ast.Declarations

let com2 = Compiler(@"C:\development\crazy\src\Shared\SharedGame.fs", proj, compOptions, "")
let ast2 =
    Fable.Transforms.FSharp2Fable.Compiler.transformFile com2 proj.ImplementationFiles
    |> Fable.Transforms.FableTransforms.optimizeFile com



let phpComp = PhpCompiler.empty
let fs = 
    [ 
      for decl in ast2.Declarations.[0..56] do
      yield! convertDecl phpComp decl
      ]


let w = new StringWriter()
let ctx = Output.Writer.create w
let file = { Decls = fs }
Output.writeFile ctx file
w.ToString()

IO.File.WriteAllText(@"C:\development\crazy\php\lib.php", string w)


