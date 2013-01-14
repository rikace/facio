﻿(*
Copyright (c) 2012-2013, Jack Pappas
All rights reserved.

This code is provided under the terms of the 2-clause ("Simplified") BSD license.
See LICENSE.TXT for licensing details.
*)

namespace FSharpYacc.Plugin

open System.ComponentModel.Composition
open System.IO
open LanguagePrimitives
open FSharpYacc
open FSharpYacc.Ast
open FSharpYacc.Compiler


/// Emit table-driven code which is compatible with the code generated
/// by the older 'fsyacc' tool from the F# PowerPack.
[<RequireQualifiedAccess>]
module private FsYacc =
    open System
    open System.CodeDom.Compiler
    open System.Diagnostics
    open Printf
    open Graham.Grammar
    open Graham.LR
    open BackendUtils.CodeGen

    //
    let [<Literal>] private defaultLexingNamespace = "Microsoft.FSharp.Text.Lexing"
    //
    let [<Literal>] private defaultParsingNamespace = "Microsoft.FSharp.Text.Parsing"
    /// The namespace where the OCaml-compatible parsers can be found.
    let [<Literal>] private ocamlParsingNamespace = "FSharp.Compatibility.OCaml.Parsing"

    /// Values used in the ACTION tables created by fsyacc.
    [<Flags>]
    type private ActionValue =
        | Shift = 0x0000us
        | Reduce = 0x4000us
        | Error = 0x8000us
        | Accept = 0xc000us
        | ActionMask = 0xc000us
        | Any = 0xffffus

    /// Converts a Graham.LR.LrParserAction into an ActionValue value (used by fsyacc).
    let private actionValue = function
        | Accept ->
            ActionValue.Accept
        | Reduce productionRuleId ->
            ActionValue.Reduce |||
            EnumOfValue (Checked.uint16 productionRuleId)
        | Shift targetStateId ->
            ActionValue.Shift |||
            EnumOfValue (Checked.uint16 targetStateId)

    //
    type private FsyaccNonterminal<'Nonterminal when 'Nonterminal : comparison> =
        //
        | Start of 'Nonterminal
        //
        | Nonterminal of 'Nonterminal

    //
    type private FsyaccTerminal<'Terminal when 'Terminal : comparison> =
        //
        | Terminal of 'Terminal
        //
        | Error

    // TEMP : Only needed until we modify Graham.LR.LrParserTable to provide the
    // production rules of the _augmented_ grammar.
    let private deaugmentSymbols (symbols : AugmentedSymbol<'Nonterminal, 'Terminal>[]) =
        symbols
        |> Array.map (function
            | Symbol.Nonterminal nonterminal ->
                match nonterminal with
                | AugmentedNonterminal.Start ->
                    failwith "Start symbol in an item which is not part of the start production."
                | AugmentedNonterminal.Nonterminal nonterminal ->
                    Symbol.Nonterminal nonterminal
            | Symbol.Terminal terminal ->
                match terminal with
                | EndOfFile ->
                    failwith "Unexpected end-of-file symbol."
                | AugmentedTerminal.Terminal terminal ->
                    Symbol.Terminal terminal)

    /// Emit a formatted string as a single-line F# comment into an IndentedTextWriter.
    let inline private comment (writer : IndentedTextWriter) fmt : ^T =
        writer.Write "// "
        fprintfn writer fmt

    /// Emits a formatted string as a quick-summary (F# triple-slash comment) into an IndentedTextWriter.
    let inline private quickSummary (writer : IndentedTextWriter) fmt : ^T =
        fmt |> Printf.ksprintf (fun str ->
            // Split the string into individual lines.
            str.Split ([| "\r\n"; "\r"; "\n" |], System.StringSplitOptions.None)
            // Write the lines to the IndentedTextWriter as triple-slash comments.
            |> Array.iter (fun line ->
                writer.Write "/// "
                writer.WriteLine (line.Trim ())
                ))

    /// <summary>Emits the declaration of a discriminated union type into an IndentedTextWriter.</summary>
    /// <param name="typeName">The name of the type.</param>
    /// <param name="isPublic">When set, the type will be publicly-visible.</param>
    /// <param name="cases">A map containing the names (and types, if applicable) of the cases.</param>
    /// <param name="writer">The IndentedTextWriter into which to write the code.</param>
    let private unionTypeDecl (typeName : string, isPublic, cases : Map<string, string option>) (writer : IndentedTextWriter) =
        // Write the type declaration
        if isPublic then
            sprintf "type %s =" typeName
        else
            sprintf "type private %s =" typeName
        |> writer.WriteLine

        // Write the type cases
        IndentedTextWriter.indented writer <| fun writer ->
            cases
            |> Map.iter (fun caseName caseType ->
                match caseType with
                | None ->
                    "| " + caseName
                | Some caseType ->
                    // NOTE : The parentheses here are actually quite important -- if the token type
                    // is a tuple, then wrapping it in parenthesis makes the F# compiler treat it as
                    // a single value. This reduces performance, but makes emitting some of the
                    // pattern-matching clauses in the other functions much simpler.
                    sprintf "| %s of (%s)" caseName caseType
                |> writer.WriteLine)

    /// Emits code which declares a literal integer value into a TextWriter.
    let private intLiteralDecl (name, isPublic, value : int) (writer : #TextWriter) =
        // Preconditions
        if System.String.IsNullOrWhiteSpace name then
            invalidArg "name" "The variable name cannot be null/empty/whitespace."

        if isPublic then
            sprintf "let [<Literal>] %s = %i" name value
        else
            sprintf "let [<Literal>] private %s = %i" name value
        |> writer.WriteLine

    /// Emits code which creates an array of UInt16 constants into a TextWriter.
    let private oneLineArrayUInt16 (name, isPublic, array : uint16[]) (writer : #TextWriter) =
        // Preconditions
        if System.String.IsNullOrWhiteSpace name then
            invalidArg "name" "The variable name cannot be null/empty/whitespace."

        // Emit the 'let' binding and opening bracket of the array.
        if isPublic then
            sprintf "let %s = [| " name
        else
            sprintf "let private %s = [| " name
        |> writer.Write

        // Emit the array values.
        array
        |> Array.iter (fun x ->
            writer.Write (x.ToString() + "us; "))

        // Emit the closing bracket of the array.
        writer.WriteLine "|]"

    /// Creates a Map whose keys are the values of the given Map.
    /// The value associated with each key is None.
    let private valueMap (map : Map<'Key, 'T>) : Map<'T, 'U option> =
        (Map.empty, map)
        ||> Map.fold (fun valueMap _ value ->
            Map.add value None valueMap)

    /// The name of the end-of-input terminal.
    let [<Literal>] private endOfInputTerminal : TerminalIdentifier = "end_of_input"
    /// The name of the error terminal.
    let [<Literal>] private errorTerminal : TerminalIdentifier = "error"

    //
    let private tablesRecordAndParserFunctions terminalCount (writer : IndentedTextWriter) =
        // Emit the 'tables' record.
        fprintfn writer "let tables () : %s.Tables<_> = {" defaultParsingNamespace
        IndentedTextWriter.indented writer <| fun writer ->
            writer.WriteLine "reductions = _fsyacc_reductions ();"
            writer.WriteLine "endOfInputTag = _fsyacc_endOfInputTag;"
            writer.WriteLine "tagOfToken = tagOfToken;"
            writer.WriteLine "dataOfToken = _fsyacc_dataOfToken;"
            writer.WriteLine "actionTableElements = _fsyacc_actionTableElements;"
            writer.WriteLine "actionTableRowOffsets = _fsyacc_actionTableRowOffsets;"
            writer.WriteLine "stateToProdIdxsTableElements = _fsyacc_stateToProdIdxsTableElements;"
            writer.WriteLine "stateToProdIdxsTableRowOffsets = _fsyacc_stateToProdIdxsTableRowOffsets;"
            writer.WriteLine "reductionSymbolCounts = _fsyacc_reductionSymbolCounts;"
            writer.WriteLine "immediateActions = _fsyacc_immediateActions;"
            writer.WriteLine "gotos = _fsyacc_gotos;"
            writer.WriteLine "sparseGotoTableRowOffsets = _fsyacc_sparseGotoTableRowOffsets;"
            writer.WriteLine "tagOfErrorTerminal = _fsyacc_tagOfErrorTerminal;"

            writer.WriteLine "parseError ="
            IndentedTextWriter.indented writer <| fun writer ->
                fprintfn writer "(fun (ctxt : %s.ParseErrorContext<_>) ->" defaultParsingNamespace
                IndentedTextWriter.indented writer <| fun writer ->
                    writer.WriteLine "match parse_error_rich with"
                    writer.WriteLine "| Some f -> f ctxt"
                    writer.WriteLine "| None -> parse_error ctxt.Message);"

            fprintfn writer "numTerminals = %i;" terminalCount
            writer.WriteLine "productionToNonTerminalTable = _fsyacc_productionToNonTerminalTable;"

            // Write the closing bracket for the record.
            writer.WriteLine "}"

        // Emit the parser functions.
        writer.WriteLine "let engine lexer lexbuf startState = (tables ()).Interpret(lexer, lexbuf, startState)"
        writer.WriteLine "let spec lexer lexbuf : Ast.ParserSpec ="
        IndentedTextWriter.indented writer <| fun writer ->
            writer.WriteLine "unbox ((tables ()).Interpret(lexer, lexbuf, 0))"

    //
    let private parserTypes (processedSpec : ProcessedSpecification<NonterminalIdentifier, TerminalIdentifier>) (writer : IndentedTextWriter) =
        // Emit the token type declaration.
        comment writer "This type is the type of tokens accepted by the parser"
        unionTypeDecl ("token", true, processedSpec.Terminals) writer
        writer.WriteLine ()

        /// Maps terminals (tokens) to symbolic token names.
        let symbolicTokenNames =
            processedSpec.Terminals
            // Add the end-of-input and error terminals to the map.
            |> Map.add endOfInputTerminal None
            |> Map.add errorTerminal None
            |> Map.map (fun terminalName _ ->
                "TOKEN_" + terminalName)

        // Emit the symbolic token-name type declaration.
        quickSummary writer "This type is used to give symbolic names to token indexes, useful for error messages."
        unionTypeDecl ("tokenId", false, valueMap symbolicTokenNames) writer
        writer.WriteLine ()

        /// Maps nonterminal identifiers to symbolic nonterminal names.
        let symbolicNonterminalNames =
            processedSpec.Nonterminals
            |> Map.map (fun nonterminalName _ ->
                "NONTERM_" + nonterminalName)

        // Emit the symbolic nonterminal type declaration.
        quickSummary writer "This type is used to give symbolic names to token indexes, useful for error messages."
        do
            /// The symbolic nonterminals. All values in this map are None
            /// since the cases don't carry values.
            let symbolicNonterminals =
                let symbolicNonterminals = valueMap symbolicNonterminalNames
                // Add cases for the starting symbols.
                (symbolicNonterminals, processedSpec.StartSymbols)
                ||> Set.fold (fun symbolicNonterminals startSymbol ->
                    Map.add ("NONTERM__start" + startSymbol) None symbolicNonterminals)

                // Write the type declaration.
            unionTypeDecl ("nonterminalId", false, symbolicNonterminals) writer
        writer.WriteLine ()

        /// Integer indices (tags) of terminals (tokens).
        let tokenTags =
            ((Map.empty, 0), processedSpec.Terminals)
            ||> Map.fold (fun (tokenTags, index) terminal _ ->
                Map.add terminal index tokenTags,
                index + 1)
            // Add the end-of-input and error terminals and discard the index value.
            |> fun (tokenTags, index) ->
                tokenTags
                |> Map.add errorTerminal index
                |> Map.add endOfInputTerminal (index + 1)
         
        // Emit the token -> token-tag function.
        quickSummary writer "Maps tokens to integer indexes."
        writer.WriteLine "let private tagOfToken = function"
        IndentedTextWriter.indented writer <| fun writer ->
            // Emit a case for each terminal (token).
            processedSpec.Terminals
            |> Map.iter (fun tokenName tokenType ->
                let tagValue = Map.find tokenName tokenTags
                match tokenType with
                | None ->
                    sprintf "| %s -> %i" tokenName tagValue
                | Some _ ->
                    sprintf "| %s _ -> %i" tokenName tagValue
                |> writer.WriteLine)
        writer.WriteLine ()

        // Emit the token-tag -> symbolic-token-name function.
        quickSummary writer "Maps integer indices to symbolic token ids."
        writer.WriteLine "let private tokenTagToTokenId = function"
        IndentedTextWriter.indented writer <| fun writer ->
            // Emit a case for each terminal (token).
            tokenTags
            |> Map.iter (fun tokenName tagValue ->
                Map.find tokenName symbolicTokenNames
                |> sprintf "| %i -> %s" tagValue
                |> writer.WriteLine)

            // Emit a catch-all case to handle invalid values.
            let catchAllVariableName = "tokenIdx"
            writer.WriteLine ("| " + catchAllVariableName + " ->")
            IndentedTextWriter.indented writer <| fun writer ->
                // When the catch-all is matched, it should raise an exception.
                writer.WriteLine (
                    "failwithf \"tokenTagToTokenId: Invalid token. (Tag = %i)\" " + catchAllVariableName)
        writer.WriteLine ()

        /// Maps production indices to the symbolic name of the nonterminal produced by each production rule.
        let productionIndices =
            let productionIndices_productionIndex =
                ((Map.empty, 0), processedSpec.StartSymbols)
                ||> Set.fold (fun (productionIndices, productionIndex) startSymbol ->
                    /// The symbolic name of the nonterminal produced by this start symbol.
                    let nonterminalName = "NONTERM__start" + startSymbol
                    
                    // Increment the production index for the next iteration.
                    Map.add productionIndex nonterminalName productionIndices,
                    productionIndex + 1)

            // Add the production rules.
            (productionIndices_productionIndex, processedSpec.ProductionRules)
            ||> Map.fold (fun productionIndices_productionIndex nonterminal rules ->
                /// The symbolic name of this nonterminal.
                let nonterminalSymbolicName = Map.find nonterminal symbolicNonterminalNames

                // OPTIMIZE : There's no need to fold over the array of rules, since the
                // only thing that matters is the _number_ of rules.
                // Replace this with a call to Range.fold.
                (productionIndices_productionIndex, rules)
                ||> Array.fold (fun (productionIndices, productionIndex) _ ->
                    Map.add productionIndex nonterminalSymbolicName productionIndices,
                    productionIndex + 1))
            // Discard the production index counter.
            |> fst

        // Emit the production-index -> symbolic-nonterminal-name function.
        quickSummary writer "Maps production indexes returned in syntax errors to strings representing
                             the non-terminal that would be produced by that production."
        writer.WriteLine "let private prodIdxToNonTerminal = function"
        IndentedTextWriter.indented writer <| fun writer ->
            // Emit cases based on the production-index -> symbolic-nonterminal-name map.
            productionIndices
            |> Map.iter (fun productionIndex nonterminalSymbolicName ->
                sprintf "| %i -> %s" productionIndex nonterminalSymbolicName
                |> writer.WriteLine)

            // Emit a catch-all case to handle invalid values.
            let catchAllVariableName = "prodIdx"
            writer.WriteLine ("| " + catchAllVariableName + " ->")
            IndentedTextWriter.indented writer <| fun writer ->
                // When the catch-all is matched, it should raise an exception.
                writer.WriteLine (
                    "failwithf \"prodIdxToNonTerminal: Invalid production index. (Index = %i)\" " + catchAllVariableName)
        writer.WriteLine ()

        // Emit constants for "end-of-input" and "tag of error terminal"
        intLiteralDecl ("_fsyacc_endOfInputTag", false,
            Map.find endOfInputTerminal tokenTags) writer
        intLiteralDecl ("_fsyacc_tagOfErrorTerminal", false,
            Map.find errorTerminal tokenTags) writer
        writer.WriteLine ()

        // Emit the token -> token-name function.
        quickSummary writer "Gets the name of a token as a string."
        writer.WriteLine "let token_to_string = function"
        IndentedTextWriter.indented writer <| fun writer ->
            // Emit a case for each terminal (token).
            processedSpec.Terminals
            |> Map.iter (fun tokenName tokenType ->
                match tokenType with
                | None ->
                    sprintf "| %s -> \"%s\"" tokenName tokenName
                | Some _ ->
                    sprintf "| %s _ -> \"%s\"" tokenName tokenName
                |> writer.WriteLine)
        writer.WriteLine ()

        // Emit the function for getting the token data.
        quickSummary writer "Gets the data carried by a token as an object."
        writer.WriteLine "let private _fsyacc_dataOfToken = function"
        IndentedTextWriter.indented writer <| fun writer ->
            // Emit a case for each terminal (token).
            processedSpec.Terminals
            |> Map.iter (fun tokenName tokenType ->
                match tokenType with
                | None ->
                    sprintf "| %s -> (null : System.Object)" tokenName
                | Some _ ->
                    sprintf "| %s _fsyacc_x -> box _fsyacc_x" tokenName
                |> writer.WriteLine)
        writer.WriteLine ()

        // Return some of the lookup tables, they're needed
        // when emitting the parser tables.
        productionIndices

    /// Emits F# code which creates the parser tables into an IndentedTextWriter.
    let private parserTables (processedSpec : ProcessedSpecification<NonterminalIdentifier, TerminalIdentifier>,
                              parserTable : Lr0ParserTable<NonterminalIdentifier, TerminalIdentifier>,
                              productionIndices : Map<_,_>) (writer : IndentedTextWriter) =
        // _fsyacc_gotos
        // _fsyacc_sparseGotoTableRowOffsets
        let _fsyacc_gotos, _fsyacc_sparseGotoTableRowOffsets =
            /// The source and target states of GOTO transitions over each nonterminal.
            let gotoEdges =
                (Map.empty, parserTable.GotoTable)
                ||> Map.fold (fun gotoEdges (source, nonterminal) target ->
                    /// The GOTO edges labeled with this nonterminal.
                    let edges =
                        match Map.tryFind nonterminal gotoEdges with
                        | None ->
                            Set.singleton (source, target)
                        | Some edges ->
                            Set.add (source, target) edges

                    // Update the map with the new set of edges for this nonterminal.
                    Map.add nonterminal edges gotoEdges)

            let startSymbolCount = Set.count processedSpec.StartSymbols
            let gotos = ResizeArray ()
            let offsets = Array.zeroCreate (startSymbolCount + gotoEdges.Count)

            // Add entries for the "fake" starting nonterminals.
            for i = 0 to startSymbolCount - 1 do
                gotos.Add 0us
                gotos.Add (EnumToValue ActionValue.Any)
                offsets.[i] <- uint16 (2 * i)

            // Add entries for the rest of the nonterminals.
            (startSymbolCount, gotoEdges)
            ||> Map.fold (fun nonterminalIndex nonterminal edges ->
                // Store the starting index (in the sparse GOTO table) for this nonterminal.
                offsets.[nonterminalIndex] <- Checked.uint16 gotos.Count

                // Add the number of edges and the "any" action to the sparse GOTOs table.
                gotos.Add (uint16 <| Set.count edges)
                gotos.Add (EnumToValue ActionValue.Any)

                // Add each of the GOTO edges to the sparse GOTO table.
                edges
                |> Set.iter (fun (source, target) ->
                    gotos.Add <| Checked.uint16 source
                    gotos.Add <| Checked.uint16 target)

                // Update the nonterminal index for the next iteration.
                nonterminalIndex + 1)
            // Discard the counter
            |> ignore

            // Convert the ResizeArray to an array and return it.
            gotos.ToArray (),
            offsets

        Debug.Assert (
            (let elementCount = Checked.uint16 <| Array.length _fsyacc_gotos in
                _fsyacc_sparseGotoTableRowOffsets
                |> Array.forall ((>) elementCount)),
            "One or more of the offsets in '_fsyacc_gotos' \
             is greater than the length of '_fsyacc_sparseGotoTableRowOffsets'.")

        oneLineArrayUInt16 ("_fsyacc_gotos", false,
            _fsyacc_gotos) writer
        oneLineArrayUInt16 ("_fsyacc_sparseGotoTableRowOffsets", false,
            _fsyacc_sparseGotoTableRowOffsets) writer


        (* _fsyacc_stateToProdIdxsTableElements *)
        (* _fsyacc_stateToProdIdxsTableRowOffsets *)
        let _fsyacc_stateToProdIdxsTableElements, _fsyacc_stateToProdIdxsTableRowOffsets =
            // OPTIMIZE : Modify Graham.LR.LrParserTable to provide the production rules
            // of the _augmented_ grammar instead of re-augmenting them here.
            let productionRuleIndices : Map<AugmentedNonterminal<_> * AugmentedSymbol<_,_>[], int> =
                let productionRuleIndices =
                    (Map.empty, processedSpec.StartSymbols)
                    ||> Set.fold (fun productionRuleIndices startSymbol ->
                        let key =
                            AugmentedNonterminal.Start,
                            [| Symbol.Nonterminal <| AugmentedNonterminal.Nonterminal startSymbol; Symbol.Terminal EndOfFile |]
                        Map.add key productionRuleIndices.Count productionRuleIndices)

                // Add the rest of the production rules.
                (productionRuleIndices, processedSpec.ProductionRules)
                ||> Map.fold (fun productionRuleIndices nonterminal rules ->
                    (productionRuleIndices, rules)
                    ||> Array.fold (fun productionRuleIndices rule ->
                        let key =
                            let symbols =
                                rule.Symbols
                                |> Array.map (function
                                    | Symbol.Nonterminal nonterminal ->
                                        AugmentedNonterminal.Nonterminal nonterminal
                                        |> Symbol.Nonterminal
                                    | Symbol.Terminal terminal ->
                                        AugmentedTerminal.Terminal terminal
                                        |> Symbol.Terminal)

                            AugmentedNonterminal.Nonterminal nonterminal, symbols

                        Map.add key productionRuleIndices.Count productionRuleIndices))

            let parserStates =
                Map.toArray parserTable.ParserStates

            let stateToProdIdxsTableElements =
                // Initialize to a reasonable size to avoid small re-allocations.
                ResizeArray (4 * parserStates.Length)

            let stateToProdIdxsTableRowOffsets =
                Array.zeroCreate <| Array.length parserStates

            // Populate the arrays from 'parserStates'.
            parserStates
            |> Array.iteri (fun idx (_, items) ->
                // Record the starting index (offset) for this state.
                stateToProdIdxsTableRowOffsets.[idx] <-
                    Checked.uint16 stateToProdIdxsTableElements.Count

                // Add the number of elements for this state to the 'elements' array.
                stateToProdIdxsTableElements.Add (Checked.uint16 <| Set.count items)

                // Store the elements for this state into the 'elements' array.
                items
                |> Set.iter (fun item ->
                    productionRuleIndices
                    |> Map.find (item.Nonterminal, item.Production)
                    |> Checked.uint16
                    |> stateToProdIdxsTableElements.Add))

            // Return the constructed arrays.
            stateToProdIdxsTableElements.ToArray (),
            stateToProdIdxsTableRowOffsets

        Debug.Assert (
            (let elementCount = Checked.uint16 <| Array.length _fsyacc_stateToProdIdxsTableElements in
                _fsyacc_stateToProdIdxsTableRowOffsets
                |> Array.forall ((>) elementCount)),
            "One or more of the offsets in '_fsyacc_stateToProdIdxsTableRowOffsets' \
             is greater than the length of '_fsyacc_stateToProdIdxsTableElements'.")

        oneLineArrayUInt16 ("_fsyacc_stateToProdIdxsTableElements", false,
            _fsyacc_stateToProdIdxsTableElements) writer
        oneLineArrayUInt16 ("_fsyacc_stateToProdIdxsTableRowOffsets", false,
            _fsyacc_stateToProdIdxsTableRowOffsets) writer


        (* _fsyacc_action_rows *)
        intLiteralDecl ("_fsyacc_action_rows", false,
            parserTable.ParserStates.Count) writer


        (* _fsyacc_actionTableElements *)
        (* _fsyacc_actionTableRowOffsets *)
        let _fsyacc_actionTableElements, _fsyacc_actionTableRowOffsets =
            //
            let actionsByState =
                // OPTIMIZE : Some of the computations below could be merged together to
                // avoid creating intermediate data structures (and avoid unnecessary calculations).
                (Map.empty, parserTable.ActionTable)
                ||> Map.fold (fun parserActionsByState (stateId, terminal) actionSet ->
                    match actionSet with
                    | Conflict _ ->
                        failwithf "Conflicting actions on terminal %O in state #%i." terminal (int stateId)
                    | Action action ->
                        let stateActions =
                            let value = terminal, action
                            match Map.tryFind stateId parserActionsByState with
                            | None ->
                                [value]
                            | Some stateActions ->
                                value :: stateActions

                        Map.add stateId stateActions parserActionsByState)

            //
            let terminalsByActionByState =
                actionsByState
                |> Map.map (fun _ actions ->
                    (Map.empty, actions)
                    ||> List.fold (fun terminalsByAction (terminal, action) ->
                        let terminals =
                            match Map.tryFind action terminalsByAction with
                            | None ->
                                Set.singleton terminal
                            | Some terminals ->
                                Set.add terminal terminals

                        Map.add action terminals terminalsByAction))

            /// The total number of parser actions (excluding implicit error actions) for each parser state.
            let actionCountByState =
                actionsByState
                |> Map.map (fun _ actions ->
                    List.length actions)                    

            /// The most-frequent parser action (if any) for each parser state.
            let mostFrequentAction =
                terminalsByActionByState
                |> Map.map (fun _ terminalsByAction ->
                    (None, terminalsByAction)
                    ||> Map.fold (fun mostFrequent action terminals ->
                        let terminalCount = Set.count terminals
                        match mostFrequent with
                        | None ->
                            Some (action, terminalCount)
                        | Some (mostFrequentAction, mostFrequentActionTerminalCount) ->
                            if terminalCount > mostFrequentActionTerminalCount then
                                Some (action, terminalCount)
                            else
                                mostFrequent)
                    |> Option.map fst
                    |> Option.get)

            //
            let compressedActionTable =
                /// Integer indices (tags) of terminals (tokens).
                // TEMP : This is computed when emitting the parser types -- pass it in
                // to this function instead of re-computing it.
                let tokenTags =
                    ((Map.empty, 0), processedSpec.Terminals)
                    ||> Map.fold (fun (tokenTags, index) terminal _ ->
                        Map.add (AugmentedTerminal.Terminal terminal) index tokenTags,
                        index + 1)
                    // Add the end-of-input and error terminals and discard the index value.
                    |> fun (tokenTags, index) ->
                        tokenTags
                        |> Map.add (AugmentedTerminal.Terminal errorTerminal) index
                        |> Map.add EndOfFile (index + 1)

                /// The set of all terminals in the augmented grammar (including the fsyacc error terminal).
                let allTerminals =
                    (Set.empty, processedSpec.Terminals)
                    ||> Map.fold (fun otherActionTerminals terminal _ ->
                        Set.add (AugmentedTerminal.Terminal terminal) otherActionTerminals)
                    // Add the error and end-of-input terminals.
                    |> Set.add (AugmentedTerminal.Terminal errorTerminal)
                    |> Set.add EndOfFile

                mostFrequentAction
                |> Map.map (fun stateId mostFrequentAction ->
                    /// The terminals for which each action is executed in this state.
                    let terminalsByAction =
                        Map.find stateId terminalsByActionByState

                    /// The terminals for which the most-frequent action is taken in this parser state.
                    let mostFrequentActionTerminals =
                        Map.find mostFrequentAction terminalsByAction

                    /// The total number of parser actions for this state, excluding implicit error actions.
                    let totalActionCount = Map.find stateId actionsByState

                    /// The terminals for which the most-frequent action is NOT taken.
                    let otherActionTerminals =
                        Set.difference allTerminals mostFrequentActionTerminals

                    // We only bother "factoring" out the most-frequent action when doing so actually saves space;
                    // i.e., when the most-frequent action covers a greater number of terminals than the implicit error action.
                    let explicitActionCount =
                        allTerminals
                        |> Set.filter (fun terminal ->
                            Map.containsKey (stateId, terminal) parserTable.ActionTable)
                        |> Set.count

                    let mostFrequentActionValue, entries =
                        let errorActionCount = (Set.count allTerminals) - explicitActionCount
                        if Set.count mostFrequentActionTerminals <= errorActionCount then
                            // No space savings, leave the error action.
                            EnumToValue ActionValue.Error,
                            Map.find stateId actionsByState
                            |> List.toArray
                            |> Array.map (fun (terminal, action) ->
                                let terminalTag = Map.find terminal tokenTags
                                let action = EnumToValue (actionValue action)
                                (Checked.uint16 terminalTag), action)
                            |> Array.sortWith (fun (tag1, _) (tag2, _) ->
                                compare tag1 tag2)
                        else
                            EnumToValue (actionValue mostFrequentAction),
                            otherActionTerminals
                            |> Set.toArray
                            |> Array.map (fun terminal ->
                                let terminalTag = Map.find terminal tokenTags
                                let action =
                                    match Map.tryFind (stateId, terminal) parserTable.ActionTable with
                                    | None ->
                                        ActionValue.Error
                                    | Some (Action action) ->
                                        actionValue action
                                    | Some (Conflict _) ->
                                        failwithf "Conflicting actions on terminal %O in state #%i." terminal (int stateId)
                                (Checked.uint16 terminalTag), (EnumToValue action))
                            |> Array.sortWith (fun (tag1, _) (tag2, _) ->
                                compare tag1 tag2)

                    // The entries for this state.
                    let elements = Array.zeroCreate <| 2 * (Array.length entries + 1)
                    elements.[0] <- Checked.uint16 <| Array.length entries
                    elements.[1] <- mostFrequentActionValue
                    
                    entries
                    |> Array.iteri (fun i (terminalTag, actionValue) ->
                        let idx = 2 * (i + 1)
                        elements.[idx] <- terminalTag
                        elements.[idx + 1] <- actionValue)
                    elements)

            let actionTableElements =
                // Initialize to roughly the correct size (to avoid multiple small reallocations).
                ResizeArray (6 * compressedActionTable.Count)

            let actionTableRowOffsets = Array.zeroCreate compressedActionTable.Count

            compressedActionTable
            |> Map.iter (fun stateId elements ->
                actionTableRowOffsets.[int stateId] <- Checked.uint16 <| actionTableElements.Count / 2
                actionTableElements.AddRange elements)

            actionTableElements.ToArray (),
            actionTableRowOffsets

        Debug.Assert (
            (let elementCount = Checked.uint16 <| Array.length _fsyacc_actionTableElements in
                _fsyacc_actionTableRowOffsets
                |> Array.forall ((>) elementCount)),
            "One or more of the offsets in '_fsyacc_actionTableRowOffsets' \
             is greater than the length of '_fsyacc_actionTableElements'.")

        oneLineArrayUInt16 ("_fsyacc_actionTableElements", false,
            _fsyacc_actionTableElements) writer
        oneLineArrayUInt16 ("_fsyacc_actionTableRowOffsets", false,
            _fsyacc_actionTableRowOffsets) writer


        (* _fsyacc_reductionSymbolCounts *)
        let _fsyacc_reductionSymbolCounts =
            let symbolCounts = ResizeArray (productionIndices.Count)

            // The productions created by the start symbols reduce a single value --
            // the start symbols (nonterminals) themselves.
            for i = 0 to (Set.count processedSpec.StartSymbols) - 1 do
                symbolCounts.Add 1us

            // Add the number of symbols in each production rule.
            processedSpec.ProductionRules
            |> Map.iter (fun _ rules ->
                rules |> Array.iter (fun rule ->
                    rule.Symbols |> Array.length |> uint16 |> symbolCounts.Add))

            // Return the symbol count.
            symbolCounts.ToArray ()

        oneLineArrayUInt16 ("_fsyacc_reductionSymbolCounts", false,
            _fsyacc_reductionSymbolCounts) writer

        (* _fsyacc_productionToNonTerminalTable *)
        let _fsyacc_productionToNonTerminalTable =
            let productionToNonTerminalTable = Array.zeroCreate productionIndices.Count

            // The augmented start symbol will always have nonterminal index 0
            // so we don't actually have to write anything to the array for the
            // elements corresponding to it's production rules (because those
            // elements will already be zeroed-out).

            // Add the nonterminal indices for each production rule.
            ((1, Set.count processedSpec.StartSymbols), processedSpec.ProductionRules)
            ||> Map.fold (fun (nonterminalIndex, prodRuleIndex) _ rules ->
                /// The number of production rules for this nonterminal.
                let ruleCount = Array.length rules

                // Set the next 'ruleCount' elements in the array to this nonterminal's index.
                for i = 0 to ruleCount - 1 do
                    productionToNonTerminalTable.[prodRuleIndex + i] <-
                        uint16 nonterminalIndex

                // Update the counters before the next iteration.
                nonterminalIndex + 1,
                prodRuleIndex + ruleCount)
            // Discard the counters.
            |> ignore

            // Return the array.
            productionToNonTerminalTable

        oneLineArrayUInt16 ("_fsyacc_productionToNonTerminalTable", false,
            _fsyacc_productionToNonTerminalTable) writer


        (* _fsyacc_immediateActions *)
        let _fsyacc_immediateActions =
            // When a state contains a single item whose parser position ("dot")
            // is at the end of the production rule, a Reduce or Accept will be
            // executed immediately upon entering the state.
            // NOTE : The length of this array should be equal to the number of parser states.
            let immediateActions = Array.zeroCreate parserTable.ParserStates.Count

            // TEMP : Remove this once we rewrite the rest of this code to work with
            // an augmented grammar instead of the "raw" grammar.
            let productionRuleIndices =
                let startSymbolCount = Set.count processedSpec.StartSymbols
                ((Map.empty, startSymbolCount), processedSpec.ProductionRules)
                ||> Map.fold (fun productionIndices_productionIndex nonterminal rules ->
                    (productionIndices_productionIndex, rules)
                    ||> Array.fold (fun (productionIndices, productionIndex) rule ->
                        Map.add (nonterminal, rule.Symbols) productionIndex productionIndices,
                        productionIndex + 1))
                // Discard the production index counter.
                |> fst

            parserTable.ParserStates
            |> Map.iter (fun parserStateId items ->
                // Set the array element corresponding to this parser state.
                immediateActions.[int parserStateId] <-
                    // Does this state contain just one (1) item?
                    if Set.count items <> 1 then
                        // Return the value which indicates this parser state has no immediate action.
                        EnumToValue ActionValue.Any
                    else
                        /// The single item in this parser state.
                        let item = Set.minElement items

                        // Is the parser position at the end of the production rule?
                        // (Or, if it's one of the starting productions -- the next-to-last position).
                        match item.Nonterminal with
                        | AugmentedNonterminal.Start when int item.Position = (Array.length item.Production - 1) ->
                            // This state should have an immediate Accept action.
                            EnumToValue ActionValue.Accept

                        | AugmentedNonterminal.Nonterminal nonterminal
                            when int item.Position = Array.length item.Production ->
                            /// The (augmented) symbols in this production rule.
                            let symbols = deaugmentSymbols item.Production

                            // The index of the production rule to reduce by.
                            Map.find (nonterminal, symbols) productionRuleIndices
                            |> Int32WithMeasure
                            // Return a value representing a Reduce action with this production rule.
                            |> Reduce
                            |> actionValue
                            |> EnumToValue

                        | _ ->
                            // Return the value which indicates this parser state has no immediate action.
                            EnumToValue ActionValue.Any)

            // Return the constructed array.
            immediateActions

        oneLineArrayUInt16 ("_fsyacc_immediateActions", false,
            _fsyacc_immediateActions) writer

    //
    let private reduction (processedSpec : ProcessedSpecification<NonterminalIdentifier, TerminalIdentifier>,
                           nonterminal : NonterminalIdentifier,
                           symbols : Symbol<NonterminalIdentifier, TerminalIdentifier>[],
                           action : CodeFragment) (writer : IndentedTextWriter) : unit =
        // Write the function declaration for this semantic action.
        fprintfn writer "(fun (parseState : %s.IParseState) ->" defaultParsingNamespace
        IndentedTextWriter.indented writer <| fun writer ->
        // Emit code to get the values of symbols carrying data values.
        symbols
        |> Array.iteri (fun idx symbol ->
            match symbol with
            | Symbol.Nonterminal nonterminal ->
                match Map.find nonterminal processedSpec.Nonterminals with
                | (Some _) as declaredType ->
                    declaredType
                | None ->
                    // Create a generic type parameter to use for this nonterminal and let
                    // the F# compiler use type inference to figure out what type it should be.
                    Some <| "'" + nonterminal
            | Symbol.Terminal terminal ->
                Map.find terminal processedSpec.Terminals
            // Emit a let-binding to get the value of this symbol if it carries any data.
            |> Option.iter (fun symbolType ->
                fprintfn writer "let _%d = (let data = parseState.GetInput(%d) in (Microsoft.FSharp.Core.Operators.unbox data : %s)) in"
                    (idx + 1) (idx + 1) symbolType))

        /// The type of the value carried by the nonterminal produced by this rule.
        let nonterminalValueType =
            match Map.tryFind nonterminal processedSpec.Nonterminals with
            | Some (Some declaredType) ->
                declaredType
            | None
            | Some None ->
                // Create a generic type parameter to use for this nonterminal and let
                // the F# compiler use type inference to figure out what type it should be.
                "'" + nonterminal
            
        // Emit the semantic action code, wrapped in a bit of code which boxes the return value.
        writer.WriteLine "Microsoft.FSharp.Core.Operators.box"
        IndentedTextWriter.indented writer <| fun writer ->
            writer.WriteLine "("
            IndentedTextWriter.indented writer <| fun writer ->
                writer.WriteLine "("
                IndentedTextWriter.indented writer <| fun writer ->
                    // TODO : May need to split 'code' into individual lines and write them to the
                    // IndentedTextWriter one-by-one to preserve correct indentation level.
                    writer.WriteLineNoTabs action
                writer.WriteLine ")"

            // Emit the nonterminal type for this production rule.
            fprintfn writer ": %s))" nonterminalValueType

    /// Replaces the placeholders for symbols in production rules
    /// (e.g., $2) with valid F# value identifiers.
    let inline private replaceSymbolPlaceholders (code : CodeFragment) =
        System.Text.RegularExpressions.Regex.Replace (code, "\$(?=\d+)", "_")

    /// Emits the user-defined semantic actions for the reductions.
    let private reductions (processedSpec : ProcessedSpecification<NonterminalIdentifier, TerminalIdentifier>)
                           (writer : IndentedTextWriter) : unit =
        /// The default action to execute when a production rule
        /// has no semantic action code associated with it.
        let defaultAction =
            sprintf "raise (%s.Accept (Microsoft.FSharp.Core.Operators.box _1))" defaultParsingNamespace

        // _fsyacc_reductions
        writer.WriteLine "let private _fsyacc_reductions () = [|"
        IndentedTextWriter.indented writer <| fun writer ->
            // Emit actions for the augmented starting nonterminals.
            processedSpec.StartSymbols
            |> Set.iter (fun startSymbol ->
                let startNonterminal = "_start" + startSymbol
                reduction (processedSpec, startNonterminal, [| Symbol.Nonterminal startSymbol |], defaultAction) writer)

            // Emit the actions for each of the production rules.
            processedSpec.ProductionRules
            |> Map.iter (fun nonterminal rules ->
                rules |> Array.iter (fun rule ->
                    let action =
                        match rule.Action with
                        | Some action ->
                            // Replace the symbol placeholders; e.g., change $2 to _2
                            replaceSymbolPlaceholders action
                        | None ->
                            defaultAction

                    reduction (processedSpec, nonterminal, rule.Symbols, action) writer))
            
            // Emit the closing bracket of the array.
            writer.WriteLine "|]"

    /// Emits code for an fsyacc-compatible parser into an IndentedTextWriter.
    let emit (processedSpec : ProcessedSpecification<NonterminalIdentifier, TerminalIdentifier>,
                parserTable : Lr0ParserTable<NonterminalIdentifier, TerminalIdentifier>)
             (options : FsyaccBackendOptions, writer : IndentedTextWriter) : unit =
        (* Emit the module declaration. *)

        quickSummary writer "Implementation file for parser generated by the fsyacc-compatibility backend for fsharpyacc."

        /// The name of the emitted parser module.
        let parserModuleName =
            defaultArg options.ModuleName "Parser"
        
        if options.InternalModule then
            fprintfn writer "module internal %s" parserModuleName
        else
            fprintfn writer "module %s" parserModuleName
        writer.WriteLine ()

        // Emit a "nowarn" directive to disable a certain type-related warning message.
        fprintf writer "#nowarn \"%i\" " 64
        comment writer "turn off warnings that type variables used in production annotations are instantiated to concrete type"
        writer.WriteLine ()

        (* Emit the "open" statements. *)
        [|  defaultLexingNamespace;
            defaultParsingNamespace + ".ParseHelpers"; |]
        |> Array.iter (fprintfn writer "open %s")
        writer.WriteLine ()

        (* Emit the header code. *)
        processedSpec.Header
        |> Option.iter (fun header ->
            // Write the header code into the TextWriter.
            // We split the code into individual lines, then write them one at a time to the
            // IndentedTextWriter; this ensures that the newlines are correct for this system
            // and also that the indentation level is correct.
            header.Split ([| "\r\n"; "\r"; "\n" |], StringSplitOptions.None)
            |> Array.iter (fun codeLine ->
                // TODO : Trim the lines? We'd have to process the entire array first
                // to determine the "base" indentation level, then trim only that much
                // from the front of each string.
                writer.WriteLine codeLine)

            writer.WriteLine ())

        // Emit the parser types (e.g., the token type).
        let productionIndices =
            parserTypes processedSpec writer

        // Emit the parser tables.
        parserTables (processedSpec, parserTable, productionIndices) writer
        reductions processedSpec writer

        // Emit the parser "tables" record and parser functions.
        let terminalCount =
            // TEMP : Add 2 to account for the end-of-input and error tokens.
            processedSpec.Terminals.Count + 2
        tablesRecordAndParserFunctions terminalCount writer

        // Flush the writer before returning, to force it to write any
        // output text remaining in it's internal buffer.
        writer.Flush ()

/// A backend which emits code implementing a table-based pattern matcher
/// compatible with 'fsyacc' and the table interpreters in the F# PowerPack.
[<Export(typeof<IBackend>)>]
type FsyaccBackend () =
    interface IBackend with
        member this.Invoke (processedSpec, parserTable, options) : unit =
            /// Compilation options specific to this backend.
            let fsyaccOptions =
                match options.FsyaccBackendOptions with
                | None ->
                    raise <| exn "No backend-specific options were provided."
                | Some options ->
                    options

            (* TODO :   This backend places an additional restriction on the parser specification --
                        all start symbols (nonterminals) must produce the same type.
                        For now, we should implement a simple check for this and raise an exception
                        if they have different types; in the future, we should find a way to pass this
                        constraint into the Compiler.precompile function so it can provide a better
                        error message for the user. *)

            // Generate the code and write it to the specified file.
            using (File.CreateText fsyaccOptions.OutputPath) <| fun streamWriter ->
                use indentedTextWriter =
                    new System.CodeDom.Compiler.IndentedTextWriter (streamWriter)
                FsYacc.emit (processedSpec, parserTable) (fsyaccOptions, indentedTextWriter)

