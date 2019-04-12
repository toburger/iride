module SparqlImplementation

open System.Reflection
open Microsoft.FSharp.Quotations
open FSharp.Core.CompilerServices
open ProviderImplementation.ProvidedTypes
open Iride
open Iride.SparqlHelper
open VDS.RDF
open VDS.RDF.Query
open VDS.RDF.Storage

[<TypeProvider>]
type BasicProvider (config : TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces 
        (config, 
         assemblyReplacementMap = [("Iride.DesignTime", "Iride")],
         addDefaultProbingLocation = true)
    let ns = "Iride"
    let asm = Assembly.GetExecutingAssembly()

    // check we contain a copy of runtime files, and are not referencing the runtime DLL
    do assert (typeof<Iride.QueryRuntime>.Assembly.GetName().Name = asm.GetName().Name)

    let getType = function
        | Node -> typeof<INode>
        | Uri -> typeof<System.Uri>
        | String -> typeof<string>
        | Integer -> typeof<int>
        | Decimal -> typeof<decimal>
        | DateTimeOffset -> typeof<System.DateTimeOffset>

    let converterMethod = function
        | Node -> typeof<QueryRuntime>.GetMethod "AsNode"
        | Uri -> typeof<QueryRuntime>.GetMethod "AsUri"
        | String -> typeof<QueryRuntime>.GetMethod "AsString"
        | Integer -> typeof<QueryRuntime>.GetMethod "AsInt"
        | Decimal -> typeof<QueryRuntime>.GetMethod "AsDecimal"
        | DateTimeOffset -> typeof<QueryRuntime>.GetMethod "AsDateTimeOffset"

    let createCtor (query: QueryDescriptor) =
        let parNames = query.input |> List.map (fun x -> x.ParameterName)
        let par = ProvidedParameter("storage", typeof<IQueryableStorage>)
        let commandText = query.commandText
        ProvidedConstructor ([par], function 
            | [storage] -> 
                <@@ QueryRuntime(%%storage, commandText, parNames) :> obj @@>
            | _ -> failwith "Expected a single parameter")
        
    let createResultType (bindings: ResultVariables) =
        
        let resultType = ProvidedTypeDefinition(asm, ns, "Result", Some typeof<obj>)
        let ctorParam = ProvidedParameter("result", typeof<SparqlResult>)
        let ctor = ProvidedConstructor([ctorParam], invokeCode = function
            | [result] -> <@@  %%result  @@>
            | _ -> failwith "Expected a single parameter")
        resultType.AddMember ctor

        bindings.Variables
        |> List.map (fun v -> ProvidedProperty(v.VariableName, getType v.Type, getterCode = function
            | [this] ->
                let varName = v.VariableName
                let node = <@@ ((%%this : obj) :?> SparqlResult).Item varName @@>
                Expr.Call(converterMethod v.Type, [node])
            | _ -> failwith "Expected a single parameter"))
        |>  List.iter resultType.AddMember

        bindings.OptionalVariables
        |> List.map (fun v -> ProvidedProperty(v.VariableName, typeof<INode option>, getterCode = function
            | [this] ->
                let varName = v.VariableName
                <@@ 
                let res = ((%%this : obj) :?> SparqlResult)
                // TODO
                if res.HasBoundValue varName then Some ((res.Item varName)) else None
                @@>
            | _ -> failwith "Expected a single parameter"))
        |>  List.iter resultType.AddMember
        resultType

    let createRunMethod (query: QueryDescriptor) resultType =
        let pars =
            query.input 
            |> List.map (fun x -> ProvidedParameter(x.ParameterName, getType x.Type))
                
        ProvidedMethod("Run", pars, resultType, invokeCode = function
            | this::pars ->
                let converters = pars |> List.map (fun par ->
                    let m = typeof<QueryRuntime>.GetMethod("ToNode", [| par.Type |])
                    Expr.Call(m, [par]))
                let array = Expr.NewArray(typeof<INode>, converters)
                match query.output with
                | QueryResult.Boolean ->
                    <@@ ((%%this: obj) :?> QueryRuntime).Ask(%%array) @@>
                | QueryResult.Graph ->
                    <@@ ((%%this: obj) :?> QueryRuntime).Construct(%%array) @@>
                | QueryResult.Bindings _ ->
                    <@@ ((%%this: obj) :?> QueryRuntime).Select(%%array) @@>
            | _ -> failwith "unexpected parameters")
 
    let createType typeName sparqlQuery =
        let providedType = ProvidedTypeDefinition(asm, ns, typeName, Some typeof<obj>)
        let query = queryDescriptor sparqlQuery

        createCtor query
        |> providedType.AddMember

        match query.output with
        | QueryResult.Boolean -> typeof<bool>
        | QueryResult.Graph -> typeof<IGraph>
        | QueryResult.Bindings bindings ->
            let resultType = createResultType bindings
            providedType.AddMember resultType
            resultType.MakeArrayType()
        |> createRunMethod query
        |> providedType.AddMember

        providedType

    let providerType = 
        let result =
            ProvidedTypeDefinition(asm, ns, "SparqlCommand", Some typeof<obj>)
        let par = ProvidedStaticParameter("SparqlQuery", typeof<string>)
        result.DefineStaticParameters([par], fun typeName args -> 
            createType typeName (string args.[0]))

        result.AddXmlDoc """<summary>TODO.</summary>
           <param name='SparqlQuery'>SPARQL parametrized query.</param>
         """
        result

    do this.AddNamespace(ns, [providerType])