﻿namespace FSharp.Data.Sql.Providers

open System
open System.IO
open System.Collections.Generic
open System.Data

open FSharp.Data.Sql
open FSharp.Data.Sql.Schema
open FSharp.Data.Sql.Common

type internal SQLiteProvider(resolutionPath, dbName) as this =
    // note we intentionally do not hang onto a connection object at any time,
    // as the type provider will dicate the connection lifecycles 
    let pkLookup =     Dictionary<string,string>()
    let tableLookup =  Dictionary<string,Table>()
    let columnLookup = Dictionary<string,Column list>()    
    let relationshipLookup = Dictionary<string,Relationship list * Relationship list>()

    // Dynamically load the SQLite assembly so we don't have a dependency on it in the project
    let assembly =  
            // we could try and load from the gac here first if no path was specified...            
            Reflection.Assembly.LoadFrom(
                if String.IsNullOrEmpty resolutionPath then "System.Data.SQLite.dll"
                else Path.GetFullPath(System.IO.Path.Combine(resolutionPath,"System.Data.SQLite.dll")))
   
    let connectionType =  (assembly.GetTypes() |> Array.find(fun t -> t.Name = "SQLiteConnection"))
    let commandType =     (assembly.GetTypes() |> Array.find(fun t -> t.Name = "SQLiteCommand"))
    let paramterType =    (assembly.GetTypes() |> Array.find(fun t -> t.Name = "SQLiteParameter"))
    let getSchemaMethod = (connectionType.GetMethod("GetSchema",[|typeof<string>|]))

    let mutable clrToEnum : (string -> DbType option)  = fun _ -> failwith "!"
    let mutable sqlToEnum : (string -> DbType option)  = fun _ -> failwith "!"
    let mutable sqlToClr :  (string -> Type option)       = fun _ -> failwith "!"

    let createTypeMappings (dt:DataTable) =        
        let clr =             
            [for r in dt.Rows -> 
                string r.["TypeName"],  unbox<int> r.["ProviderDbType"], string r.["DataType"]]

        // create map from sql name to clr type, and type to SqlDbType enum
        let sqlToClr', sqlToEnum', clrToEnum' =
            clr
            |> List.choose( fun (tn,ev,dt) ->
                if String.IsNullOrWhiteSpace dt then None else
                let ty = Type.GetType dt 
                // as far as I can see, SQLite maps ProviderDbType straight to DBType
                let ev = enum<DbType> ev                
                Some ((tn,ty),(tn,ev),(ty.FullName,ev)))
            |> fun x ->  
                let fst (x,_,_) = x
                let snd (_,y,_) = y
                let trd (_,_,z) = z
                (Map.ofList (List.map fst x), 
                 Map.ofList (List.map snd x),
                 Map.ofList (List.map trd x))

        // set lookup functions         
        sqlToClr <-  (fun name -> Map.tryFind name sqlToClr')
        sqlToEnum <- (fun name -> Map.tryFind name sqlToEnum' )
        clrToEnum <- (fun name -> Map.tryFind name clrToEnum' )
    
    let executeSql (con:IDbConnection) sql =        
        use com = (this:>ISqlProvider).CreateCommand(con,sql)    
        com.ExecuteReader()

    interface ISqlProvider with
        member __.CreateConnection(connectionString) = 
          let actualConString = 
            if String.IsNullOrEmpty connectionString
            then let path = Path.GetFullPath(Path.Combine(resolutionPath, dbName))
                 sprintf "Data source=%s;Version=3" path
            else connectionString
          Activator.CreateInstance(connectionType,[|box actualConString|]) :?> IDbConnection
        member __.CreateCommand(connection,commandText) =  Activator.CreateInstance(commandType,[|box commandText;box connection|]) :?> IDbCommand
        member __.CreateCommandParameter(name,value,dbType, direction, length) = 
            let p = Activator.CreateInstance(paramterType,[|box name;box value|]) :?> IDbDataParameter
            if dbType.IsSome then p.DbType <- dbType.Value 
            if direction.IsSome then p.Direction <- direction.Value
            if length.IsSome then p.Size <- length.Value
            upcast p
        member __.CreateTypeMappings(con) = 
            if con.State <> ConnectionState.Open then con.Open()
            let dt = getSchemaMethod.Invoke(con,[|"DataTypes"|]) :?> DataTable
            let ret = createTypeMappings dt
            con.Close()
            ret
        member __.ClrToEnum = clrToEnum
        member __.SqlToEnum = sqlToEnum
        member __.SqlToClr = sqlToClr
        member __.GetTables(con) =            
            if con.State <> ConnectionState.Open then con.Open()
            let ret =
                [ for row in (getSchemaMethod.Invoke(con,[|"Tables"|]) :?> DataTable).Rows do 
                    let ty = string row.["TABLE_TYPE"]
                    if ty <> "SYSTEM_TABLE" then
                        let table = { Schema = string row.["TABLE_CATALOG"] ; Name = string row.["TABLE_NAME"]; Type=ty } 
                        if tableLookup.ContainsKey table.FullName = false then tableLookup.Add(table.FullName,table)
                        yield table ]
            con.Close()
            ret
        member __.GetPrimaryKey(table) = 
            match pkLookup.TryGetValue table.FullName with 
            | true, v -> Some v
            | _ -> None
        member __.GetColumns(con,table) = 
            match columnLookup.TryGetValue table.FullName with
            | (true,data) -> data
            | _ -> 
               if con.State <> ConnectionState.Open then con.Open()
               let query = sprintf "pragma table_info(%s)" table.Name
               use com = (this:>ISqlProvider).CreateCommand(con,query)               
               use reader = com.ExecuteReader()
               let columns =
                  [ while reader.Read() do 
                      let dt = reader.GetString(2).ToLower()
                      let dt = if dt.Contains("(") then dt.Substring(0,dt.IndexOf("(")) else dt
                      match sqlToClr dt, sqlToEnum dt with
                      | Some(clr),Some(sql) ->
                         let col =
                            { Column.Name = reader.GetString(1); 
                              ClrType = clr
                              DbType = sql
                              IsNullable = not <| reader.GetBoolean(3); 
                              IsPrimarKey = if reader.GetBoolean(5) then true else false } 
                         if col.IsPrimarKey && pkLookup.ContainsKey table.FullName = false then pkLookup.Add(table.FullName,col.Name)
                         yield col 
                      | _ -> ()]  
               columnLookup.Add(table.FullName,columns)
               con.Close()
               columns
        member __.GetRelationships(con,table) =
            match relationshipLookup.TryGetValue(table.FullName) with
            | true,v -> v
            | _ ->
                // SQLite doesn't have great metadata capabilities.
                // while we can use PRGAMA FOREIGN_KEY_LIST, it will only show us 
                // relationships in one direction, the only way to get all the relationships
                // is to retrieve all the relationships in the entire database.  This is not ideal for
                // huge schemas, but SQLite is not generally used for that purpose so we should be ok.
                // At least we can perform all the work for all the tables once here
                // and cache the results for successive calls.....
                if con.State <> ConnectionState.Open then con.Open()
                let relData = (getSchemaMethod.Invoke(con,[|"ForeignKeys"|]) :?> DataTable)
                for row in relData.Rows do
                    let pTable = 
                        { Schema = string row.["FKEY_TO_CATALOG"]     //I've not seen a schema column populated in SQLite so I'm using catalog instead
                          Name = string row.["FKEY_TO_TABLE"] 
                          Type = ""}
                    let fTable = 
                        { Schema = string row.["TABLE_CATALOG"]   
                          Name = string row.["TABLE_NAME"] 
                          Type = ""}

                    if not <| relationshipLookup.ContainsKey pTable.FullName then relationshipLookup.Add(pTable.FullName,([],[]))
                    if not <| relationshipLookup.ContainsKey fTable.FullName then relationshipLookup.Add(fTable.FullName,([],[]))
                    
                    let rel = { Name = string row.["CONSTRAINT_NAME"]; PrimaryTable= pTable.FullName; PrimaryKey=string row.["FKEY_TO_COLUMN"]
                                ForeignTable=fTable.FullName; ForeignKey=string row.["FKEY_FROM_COLUMN"] } 

                    let (c,p) = relationshipLookup.[pTable.FullName]
                    relationshipLookup.[pTable.FullName] <- (rel::c,p)
                    let (c,p) = relationshipLookup.[fTable.FullName]
                    relationshipLookup.[fTable.FullName] <- (c,rel::p)
                con.Close()
                match relationshipLookup.TryGetValue table.FullName with 
                | true,v -> v
                | _ -> [],[]
                
        
        /// SQLite does not support stored procedures.
        member __.GetSprocs(con) = [] 

        member this.GetIndividualsQueryText(table,amount) = sprintf "SELECT * FROM %s LIMIT %i;" table.FullName amount 
        member this.GetIndividualQueryText(table,column) = sprintf "SELECT * FROM [%s].[%s] WHERE [%s].[%s].[%s] = @id" table.Schema table.Name table.Schema table.Name column

        member this.GenerateQueryText(sqlQuery,baseAlias,baseTable,projectionColumns) = 
            let sb = System.Text.StringBuilder()
            let parameters = ResizeArray<_>()
            let (~~) (t:string) = sb.Append t |> ignore
            
            // all tables should be aliased.
            // the LINQ infrastructure will cause this will happen by default if the query includes more than one table
            // if it does not, then we first need to create an alias for the single table
            let getTable x =
                match sqlQuery.Aliases.TryFind x with
                | Some(a) -> a
                | None -> baseTable

            let singleEntity = sqlQuery.Aliases.Count = 0
            
            // first build  the select statement, this is easy ...
            let columns = 
                String.Join(",",
                    [|for KeyValue(k,v) in projectionColumns do
                        if v.Count = 0 then   // if no columns exist in the projection then get everything
                            for col in columnLookup.[(getTable k).FullName] |> List.map(fun c -> c.Name) do 
                                if singleEntity then yield sprintf "[%s].[%s] as '%s'" k col col
                                else yield sprintf "[%s].[%s] as '[%s].[%s]'" k col k col
                        else
                            for col in v do 
                                if singleEntity then yield sprintf "[%s].[%s] as '%s'" k col col
                                else yield sprintf "[%s].[%s] as '[%s].[%s]'" k col k col|]) // F# makes this so easy :)
        
            // next up is the filter expressions
            // NOTE: really need to assign the parameters their correct db types
            let param = ref 0
            let nextParam() =
                incr param
                sprintf "@param%i" !param

            let createParam (value:obj) =
                let paramName = nextParam()
                (this:>ISqlProvider).CreateCommandParameter(paramName,value,None, None, None)

            let rec filterBuilder = function 
                | [] -> ()
                | (cond::conds) ->
                    let build op preds (rest:Condition list option) =
                        ~~ "("
                        preds |> List.iteri( fun i (alias,col,operator,data) ->
                                let extractData data = 
                                     match data with
                                     | Some(x) when (box x :? string array) -> 
                                         // in and not in operators pass an array
                                         let strings = box x :?> string array
                                         strings |> Array.map createParam
                                     | Some(x) -> [|createParam (box x)|]
                                     | None ->    [|createParam DBNull.Value|]

                                let prefix = if i>0 then (sprintf " %s " op) else ""
                                let paras = extractData data
                                ~~(sprintf "%s%s" prefix <|
                                    match operator with
                                    | FSharp.Data.Sql.IsNull -> (sprintf "[%s].[%s] IS NULL") alias col 
                                    | FSharp.Data.Sql.NotNull -> (sprintf "[%s].[%s] IS NOT NULL") alias col 
                                    | FSharp.Data.Sql.In ->                                     
                                        let text = String.Join(",",paras |> Array.map (fun p -> p.ParameterName))
                                        Array.iter parameters.Add paras
                                        (sprintf "[%s].[%s] IN (%s)") alias col text
                                    | FSharp.Data.Sql.NotIn ->                                    
                                        let text = String.Join(",",paras |> Array.map (fun p -> p.ParameterName))
                                        Array.iter parameters.Add paras
                                        (sprintf "[%s].[%s] NOT IN (%s)") alias col text 
                                    | _ -> 
                                        parameters.Add paras.[0]
                                        (sprintf "[%s].[%s]%s %s") alias col 
                                         (operator.ToString()) paras.[0].ParameterName)
                        )
                        // there's probably a nicer way to do this
                        let rec aux = function
                            | x::[] when preds.Length > 0 ->
                                ~~ (sprintf " %s " op)
                                filterBuilder [x]
                            | x::[] -> filterBuilder [x]
                            | x::xs when preds.Length > 0 ->
                                ~~ (sprintf " %s " op)
                                filterBuilder [x]
                                ~~ (sprintf " %s " op)
                                aux xs 
                            | x::xs ->
                                filterBuilder [x]
                                ~~ (sprintf " %s " op)
                                aux xs
                            | [] -> ()
                    
                        Option.iter aux rest
                        ~~ ")"
                
                    match cond with
                    | Or(preds,rest) -> build "OR" preds rest
                    | And(preds,rest) ->  build "AND" preds rest 
                
                    filterBuilder conds
                
            // next up is the FROM statement which includes joins .. 
            let fromBuilder() = 
                sqlQuery.Links
                |> List.iter(fun (fromAlias, data, destAlias)  ->
                    let joinType = if data.OuterJoin then "LEFT OUTER JOIN " else "INNER JOIN "
                    let destTable = getTable destAlias
                    ~~  (sprintf "%s [%s].[%s] as [%s] on [%s].[%s] = [%s].[%s] " 
                            joinType destTable.Schema destTable.Name destAlias 
                            (if data.RelDirection = RelationshipDirection.Parents then fromAlias else destAlias)
                            data.ForeignKey  
                            (if data.RelDirection = RelationshipDirection.Parents then destAlias else fromAlias) 
                            data.PrimaryKey))

            let orderByBuilder() =
                sqlQuery.Ordering
                |> List.iteri(fun i (alias,column,desc) -> 
                    if i > 0 then ~~ ", "
                    ~~ (sprintf "[%s].[%s]%s" alias column (if not desc then "DESC" else "")))

            // SELECT
            if sqlQuery.Distinct then ~~(sprintf "SELECT DISTINCT %s " columns)
            elif sqlQuery.Count then ~~("SELECT COUNT(1) ")
            else  ~~(sprintf "SELECT %s " columns)
            // FROM
            ~~(sprintf "FROM %s as %s " baseTable.FullName baseAlias)         
            fromBuilder()
            // WHERE
            if sqlQuery.Filters.Length > 0 then
                // each filter is effectively the entire contents of each where clause in the LINQ  query,
                // of which there can be many. Simply turn them all into one big AND expression as that is the
                // only logical way to deal with them. 
                let f = [And([],Some sqlQuery.Filters)]
                ~~"WHERE " 
                filterBuilder f
        
            if sqlQuery.Ordering.Length > 0 then
                ~~"ORDER BY "
                orderByBuilder()

            if sqlQuery.Take.IsSome then 
                ~~(sprintf " LIMIT %i;" sqlQuery.Take.Value)

            let sql = sb.ToString()
            (sql,parameters)

        member this.ProcessUpdates(con, entities) =
            let sb = Text.StringBuilder()
            let (~~) (t:string) = sb.Append t |> ignore

            // ensure columns have been loaded
            entities |> List.map(fun e -> e.Table) 
                     |> Seq.distinct 
                     |> Seq.iter(fun t -> (this :> ISqlProvider).GetColumns(con,t) |> ignore )

            con.Open()
            let createInsertCommand (entity:SqlEntity) =                 
                let cmd = (this :> ISqlProvider).CreateCommand(con,"")
                cmd.Connection <- con 
                let pk = pkLookup.[entity.Table.FullName] 
                let columnNames, values = 
                    (([],0),entity.ColumnValues)
                    ||> Seq.fold(fun (out,i) (k,v) -> 
                        let name = sprintf "@param%i" i
                        let p = (this :> ISqlProvider).CreateCommandParameter(name,v,None, None, None)
                        (k,p)::out,i+1)
                    |> fun (x,_)-> x 
                    |> List.rev
                    |> List.toArray 
                    |> Array.unzip
                
                sb.Clear() |> ignore
                ~~(sprintf "INSERT INTO %s (%s) VALUES (%s); SELECT last_insert_rowid();" 
                    entity.Table.FullName
                    (String.Join(",",columnNames))
                    (String.Join(",",values |> Array.map(fun p -> p.ParameterName))))
                
                values |> Array.iter (cmd.Parameters.Add >> ignore)
                cmd.CommandText <- sb.ToString()
                cmd

            let createUpdateCommand (entity:SqlEntity) changedColumns =
                let cmd = (this :> ISqlProvider).CreateCommand(con,"")
                cmd.Connection <- con 
                let pk = pkLookup.[entity.Table.FullName] 
                sb.Clear() |> ignore

                if changedColumns |> List.exists ((=)pk) then failwith "Error - you cannot change the primary key of an entity."

                let pkValue = 
                    match entity.GetColumnOption<obj> pk with
                    | Some v -> v
                    | None -> failwith "Error - you cannot update an entity that does not have a primary key."
                
                let data = 
                    (([],0),changedColumns)
                    ||> List.fold(fun (out,i) col ->                                                         
                        let name = sprintf "@param%i" i
                        let p = 
                            match entity.GetColumnOption<obj> col with
                            | Some v -> (this :> ISqlProvider).CreateCommandParameter(name,v,None, None, None)
                            | None -> (this :> ISqlProvider).CreateCommandParameter(name,DBNull.Value, None, None, None)
                        (col,p)::out,i+1)
                    |> fun (x,_)-> x 
                    |> List.rev
                    |> List.toArray 
                    
                
                let pkParam = (this :> ISqlProvider).CreateCommandParameter("@pk", pkValue, None, None, None)

                ~~(sprintf "UPDATE %s SET %s WHERE %s = @pk;" 
                    entity.Table.FullName
                    (String.Join(",", data |> Array.map(fun (c,p) -> sprintf "%s = %s" c p.ParameterName ) ))
                    pk)

                data |> Array.map snd |> Array.iter (cmd.Parameters.Add >> ignore)
                cmd.Parameters.Add pkParam |> ignore
                cmd.CommandText <- sb.ToString()
                cmd
            
            let createDeleteCommand (entity:SqlEntity) =
                let cmd = (this :> ISqlProvider).CreateCommand(con,"")
                cmd.Connection <- con 
                sb.Clear() |> ignore
                let pk = pkLookup.[entity.Table.FullName] 
                sb.Clear() |> ignore
                let pkValue = 
                    match entity.GetColumnOption<obj> pk with
                    | Some v -> v
                    | None -> failwith "Error - you cannot delete an entity that does not have a primary key."
                let p = (this :> ISqlProvider).CreateCommandParameter("@id",pkValue,None, None, None)
                cmd.Parameters.Add(p) |> ignore
                ~~(sprintf "DELETE FROM %s WHERE %s = @id" entity.Table.FullName pk )
                cmd.CommandText <- sb.ToString()
                cmd

            use scope = new Transactions.TransactionScope()
            try
                
                if con.State <> ConnectionState.Open then con.Open()         
                // initially supporting update/create/delete of single entities, no hierarchies yet
                entities
                |> List.iter(fun e -> 
                    match e._State with
                    | Created -> 
                        let cmd = createInsertCommand e
                        Common.QueryEvents.PublishSqlQuery cmd.CommandText
                        let id = cmd.ExecuteScalar()
                        match e.GetColumnOption pkLookup.[e.Table.FullName] with
                        | Some v -> () // if the primary key exists, do nothing
                                       // this is because non-identity columns will have been set 
                                       // manually and in that case scope_identity would bring back 0 "" or whatever
                        | None ->  e.SetColumnSilent(pkLookup.[e.Table.FullName], id)
                        e._State <- Unchanged
                    | Modified fields -> 
                        let cmd = createUpdateCommand e fields
                        Common.QueryEvents.PublishSqlQuery cmd.CommandText
                        cmd.ExecuteNonQuery() |> ignore
                        e._State <- Unchanged
                    | Deleted -> 
                        let cmd = createDeleteCommand e
                        Common.QueryEvents.PublishSqlQuery cmd.CommandText
                        cmd.ExecuteNonQuery() |> ignore
                        // remove the pk to prevent this attempting to be used again
                        e.SetColumnOptionSilent(pkLookup.[e.Table.FullName], None)
                    | Unchanged -> failwith "Unchanged entity encountered in update list - this should not be possible!")
                scope.Complete()
            finally
                con.Close()