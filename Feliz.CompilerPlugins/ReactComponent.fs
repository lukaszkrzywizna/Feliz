﻿namespace Feliz

open Fable
open Fable.AST
open Fable.AST.Fable

// Tell Fable to scan for plugins in this assembly
[<assembly:ScanForPlugins>]
do()

module internal ReactComponentHelpers =
    let (|ReactMemo|_|) = function
        | Import({ Selector = "memo"; Path = "react" },_,_) as e -> Some e
        | Get(Import({ Path = "react" },_,_),kind,_,_) as e ->
            match kind with
            | ExprGet(Value(StringConstant "memo",_)) -> Some e
            | FieldGet i when i.Name = "memo" -> Some e
            | _ -> None
        | _ -> None

    let injectReactImport body =
        let body = match body with Sequential body -> body | _ -> [body]
        Sequential [
            AstUtils.makeImport "default as React" "react"
            yield! body
        ]

    let applyImportOrMemo import from memo (decl: MemberDecl) =
        match import, from, memo with
        | Some _, Some _, _ ->
            let reactElType = decl.Body.Type
            // imported component doesn't need to emit any JS
            let tags = "remove-declaration" :: decl.Tags
            { decl with Body = AstUtils.emptyReactElement reactElType; Tags = tags }

        | _, _, Some true ->
            let memoFn = AstUtils.makeImport "memo" "react"
            let body =
                decl.Body
                |> injectReactImport 
                |> fun body -> [Delegate(decl.Args, body, None, Tags.empty)]
                |> AstUtils.makeCall memoFn
            // Change declaration kind from function to value
            let info =
                AstUtils.memberName decl.MemberRef
                |> AstUtils.makeMemberInfo false body.Type
                |> GeneratedValue
                |> GeneratedMemberRef
            { decl with MemberRef = info; Args = []; Body = body }

        | _ -> { decl with Body = injectReactImport decl.Body }
        
    let rewriteArgs (decl: MemberDecl) =
      // rewrite all other arguments into getters of a single props object
      // TODO: transform any callback into into useCallback(callback) to stabilize reference
      let propsArg = AstUtils.makeIdent (sprintf "%sInputProps" (AstUtils.camelCase decl.Name))
      let propBindings =
          ([], decl.Args) ||> List.fold (fun bindings arg ->
              let getterKey = if arg.DisplayName = "key" then "$key" else arg.DisplayName
              let getterKind = ExprGet(AstUtils.makeStrConst getterKey)
              let getter = Get(IdentExpr propsArg, getterKind, Any, None)
              (arg, getter)::bindings)
          |> List.rev

      let body =
          match decl.Body with
          // If the body is surrounded by a memo call we put the bindings within the call
          // because Fable will later move the surrounding function into memo
          | Call(ReactMemo reactMemo, ({ Args = arg::restArgs } as callInfo), t, r) ->
              let arg = propBindings |> List.fold (fun body (k,v) -> Let(k, v, body)) arg
              Call(reactMemo, { callInfo with Args = arg::restArgs }, t, r)
          | _ ->
              propBindings |> List.fold (fun body (k,v) -> Let(k, v, body)) decl.Body

      { decl with Args = [propsArg]; Body = body }

open ReactComponentHelpers

/// <summary>Transforms a function into a React function component. Make sure the function is defined at the module level</summary>
type ReactComponentAttribute(?exportDefault: bool, ?import: string, ?from:string, ?memo: bool) =
    inherit MemberDeclarationPluginAttribute()
    override _.FableMinimumVersion = "4.0"
    new() = ReactComponentAttribute(exportDefault=false)
    new(exportDefault: bool) = ReactComponentAttribute(exportDefault=exportDefault,?import=None, ?from=None)
    new(import: string, from: string) = ReactComponentAttribute(exportDefault=false,import=import, from=from)

    /// <summary>Transforms call-site into createElement calls</summary>
    override this.TransformCall(compiler, memb, expr) =
        let reactElType = expr.Type
        let membArgs = memb.CurriedParameterGroups |> List.concat
        match expr with
        | Call(callee, info, _typeInfo, _range) ->
            let reactComponent =
                match import, from with
                | Some importedMember, Some externalModule ->
                    AstUtils.makeImport importedMember externalModule
                | _ ->
                    callee

            if List.length membArgs = info.Args.Length && info.Args.Length = 1 && AstUtils.isRecord compiler info.Args[0].Type then
                
                // declared record
                // https://github.com/Zaid-Ajaj/Feliz/issues/603
                // F# Component { Value = 1 }
                // JSX <Component props={ Value={1} } />
                // JS createElement(Component, props = { Value: 1 })
                
                // anonymous record
                // F# Component {| Value = 1 |}
                // JSX <Component Value={1} />
                // JS createElement(Component, { Value: 1 })
                
                let isDeclaredRecord = AstUtils.isDeclaredRecord compiler info.Args[0].Type
                
                match AstUtils.tryGetRecordField "key" compiler info.Args[0].Type with
                | Some keyField when isDeclaredRecord ->
                    // When the key property is upper-case (which is common in record fields)
                    // then we should rewrite it
                    let modifiedRecord =
                        AstUtils.emitJs
                          (sprintf "(($value) => { $value.key = $value.%s.%s; return $value; })($0)" (membArgs[0].Name.Value) keyField)
                          [ AstUtils.objExpr [ membArgs[0].Name.Value, info.Args[0]] ]
                    AstUtils.createElement reactElType [reactComponent; modifiedRecord]
                | Some "Key" -> // anonymous record won't have wrapped object so 'key' (lowercase) prop is automatically recognized
                    let modifiedRecord =
                      AstUtils.emitJs "(($value) => { $value.key = $value.Key; return $value; })($0)" [info.Args[0]]
                    AstUtils.createElement reactElType [reactComponent; modifiedRecord]
                | _ ->
                    let value =
                      if isDeclaredRecord then
                        AstUtils.objExpr [ membArgs[0].Name.Value, info.Args[0] ]
                      else
                        info.Args[0]
                    AstUtils.createElement reactElType [reactComponent; value]
            elif info.Args.Length = 1 && info.Args[0].Type = Type.Unit then
                // F# Component()
                // JSX <Component />
                // JS createElement(Component, null)
                AstUtils.createElement reactElType [reactComponent; AstUtils.nullValue]
            else
                let mutable keyBinding = None

                let propsObj =
                    info.Args
                    |> List.zip (List.take info.Args.Length membArgs) 
                    |> List.collect (fun (arg, expr) ->
                        match arg.Name, expr with
                        | Some "key", IdentExpr _ -> ["key", expr; "$key", expr]
                        | Some "key", _ ->
                            let keyIdent = AstUtils.makeUniqueIdent "key"
                            keyBinding <- Some(keyIdent, expr)
                            ["key", IdentExpr keyIdent; "$key", IdentExpr keyIdent]
                        | Some name, _ -> [name, expr]
                        | None, _ -> [])
                    |> AstUtils.objExpr

                let reactEl = AstUtils.createElement reactElType [reactComponent; propsObj]
                let expr =
                    match keyBinding with
                    | None -> reactEl
                    | Some(ident, value) -> Let(ident, value, reactEl)                
                
                match [|memo, callee|] with
                // If the call is memo and the function has an identifier, we can set the displayName
                | [|(Some true), (IdentExpr i)|] -> 
                    Sequential [
                        (AstUtils.makeSet (IdentExpr(i)) "displayName" (AstUtils.makeStrConst i.Name))
                        expr
                    ]
                | _ -> expr
        | _ ->
            // return expression as is when it is not a call expression
            expr

    override this.Transform(compiler, file, decl) =
        let info = compiler.GetMember(decl.MemberRef)
        if info.IsValue || info.IsGetter || info.IsSetter then
            // Invalid attribute usage
            let errorMessage = sprintf "Expecting a function declaration for %s when using [<ReactComponent>]" decl.Name
            compiler.LogWarning(errorMessage, ?range=decl.Body.Range)
            decl
        else if not (AstUtils.isReactElement decl.Body.Type) then
            // output of a React function component must be a ReactElement
            let errorMessage = sprintf "Expected function %s to return a ReactElement when using [<ReactComponent>]. Instead it returns %A" decl.Name decl.Body.Type
            compiler.LogWarning(errorMessage, ?range=decl.Body.Range)
            decl
        else
            if (AstUtils.isCamelCase decl.Name) then
                compiler.LogWarning(sprintf "React function component '%s' is written in camelCase format. Please consider declaring it in PascalCase (i.e. '%s') to follow conventions of React applications and allow tools such as react-refresh to pick it up." decl.Name (AstUtils.capitalize decl.Name))

            let decl =
                match exportDefault with
                | Some true -> { decl with Tags = "export-default"::decl.Tags }
                | Some false | None -> decl

            // do not rewrite components accepting anonymous records as input
            if decl.Args.Length = 1 && AstUtils.isAnonymousRecord decl.Args.[0].Type then
                if AstUtils.tryGetRecordField "key" compiler decl.Args[0].Type = Some "key" then
                  let errorMessage =
                    sprintf "The function %s expects an anonymous record with a 'key' property, which is not allowed by React. The value will be erased and it will return undefined. More info: https://reactjs.org/link/special-props" decl.Name
                  compiler.LogWarning(errorMessage, ?range=decl.Body.Range)
                
                decl
                |> applyImportOrMemo import from memo
            // put record into a single props object to stabilize prototype chain
            // https://github.com/Zaid-Ajaj/Feliz/issues/603
            elif decl.Args.Length = 1 && AstUtils.isDeclaredRecord compiler decl.Args[0].Type then
                // check whether the record type is defined in this file
                // trigger warning if that is case
                let definedInThisFile =
                    file.Declarations
                    |> List.tryPick (fun declaration ->
                        match declaration with
                        | Declaration.ClassDeclaration classDecl ->
                            let classEntity = compiler.GetEntity(classDecl.Entity)
                            match decl.Args[0].Type with
                            | Type.DeclaredType (entity, _genericArgs) ->
                                let declaredEntity = compiler.GetEntity(entity)
                                if classEntity.IsFSharpRecord && declaredEntity.FullName = classEntity.FullName
                                then Some declaredEntity.FullName
                                else None

                            | _ -> None

                        | Declaration.ActionDeclaration _action ->
                            None
                        | _ ->
                            None
                    )

                match definedInThisFile with
                | Some recordTypeName ->
                    let errorMsg = String.concat "" [
                        sprintf "Function component '%s' is using a record type '%s' as an input parameter. " decl.Name recordTypeName
                        "This happens to break React tooling like react-refresh and hot module reloading. "
                        "To fix this issue, consider using an anonymous record instead or multiple simpler values as input parameters (can be tupled). "
                        "Future versions of [<ReactComponent>] might not emit this warning anymore, in which case you can assume that the issue is fixed. "
                        "To learn more about the issue, see https://github.com/pmmmwh/react-refresh-webpack-plugin/issues/258"
                    ]

                    compiler.LogWarning(errorMsg, ?range=decl.Body.Range)

                | None ->
                    // nothing to report
                    ()

                decl
                |> rewriteArgs
                |> applyImportOrMemo import from memo
            else if decl.Args.Length = 1 && decl.Args[0].Type = Type.Unit then
                // remove arguments from functions requiring unit as input
                { decl with Args = [ ] }
                |> applyImportOrMemo import from memo
            else
                decl
                |> rewriteArgs
                |> applyImportOrMemo import from memo

type ReactMemoComponentAttribute(?exportDefault: bool) =
    inherit ReactComponentAttribute(?exportDefault=exportDefault, ?import=None, ?from=None, memo=true)
    new() = ReactMemoComponentAttribute(exportDefault=false)

