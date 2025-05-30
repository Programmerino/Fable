// Copyright (c) Microsoft Corporation. All Rights Reserved. See License.txt in the project root for license information.

module internal FSharp.Compiler.AbstractIL.ILBinaryWriter

open System
open System.Collections.Generic
open System.IO

open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.Diagnostics
open FSharp.Compiler.AbstractIL.BinaryConstants
open FSharp.Compiler.AbstractIL.Support
open Internal.Utilities.Library
open FSharp.Compiler.AbstractIL.StrongNameSign
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.IO
open FSharp.Compiler.Text.Range

//---------------------------------------------------------------------
// Byte, byte array fragments and other concrete representations
// manipulations.
//---------------------------------------------------------------------

// Little-endian encoding of int32
let b0 n = byte (n &&& 0xFF)
let b1 n = byte ((n >>> 8) &&& 0xFF)
let b2 n = byte ((n >>> 16) &&& 0xFF)
let b3 n = byte ((n >>> 24) &&& 0xFF)

// Little-endian encoding of int64
let dw7 n = byte ((n >>> 56) &&& 0xFFL)
let dw6 n = byte ((n >>> 48) &&& 0xFFL)
let dw5 n = byte ((n >>> 40) &&& 0xFFL)
let dw4 n = byte ((n >>> 32) &&& 0xFFL)
let dw3 n = byte ((n >>> 24) &&& 0xFFL)
let dw2 n = byte ((n >>> 16) &&& 0xFFL)
let dw1 n = byte ((n >>> 8) &&& 0xFFL)
let dw0 n = byte (n &&& 0xFFL)

let bitsOfSingle (x: float32) = BitConverter.ToInt32(BitConverter.GetBytes x, 0)
let bitsOfDouble (x: float) = BitConverter.DoubleToInt64Bits x

/// Arbitrary value
[<Literal>]
let EmitBytesViaBufferCapacity = 10
let emitBytesViaBuffer f = use bb = ByteBuffer.Create EmitBytesViaBufferCapacity in f bb; bb.AsMemory().ToArray()

/// Alignment and padding
let align alignment n = ((n + alignment - 1) / alignment) * alignment


/// Maximum number of methods in a dotnet type
/// This differs from the spec and file formats slightly which suggests 0xfffe is the maximum
/// this value was identified empirically.
[<Literal>]
let maximumMethodsPerDotNetType = 0xfff0

//---------------------------------------------------------------------
// Concrete token representations etc. used in PE files
//---------------------------------------------------------------------

type ByteBuffer with

    /// Z32 = compressed unsigned integer
    static member Z32Size n =
      if n <= 0x7F then 1
      elif n <= 0x3FFF then 2
      else 4

    /// Emit int32 as compressed unsigned integer
    member buf.EmitZ32 n =
        if n >= 0 && n <= 0x7F then
            buf.EmitIntAsByte n
        elif n >= 0x80 && n <= 0x3FFF then
            buf.EmitIntAsByte (0x80 ||| (n >>> 8))
            buf.EmitIntAsByte (n &&& 0xFF)
        else
            buf.EmitIntAsByte (0xC0 ||| ((n >>> 24) &&& 0xFF))
            buf.EmitIntAsByte ((n >>> 16) &&& 0xFF)
            buf.EmitIntAsByte ((n >>> 8) &&& 0xFF)
            buf.EmitIntAsByte (n &&& 0xFF)

    member buf.EmitPadding n =
        for i = 0 to n-1 do
            buf.EmitByte 0x0uy

    // Emit compressed untagged integer
    member buf.EmitZUntaggedIndex big idx =
        if big then buf.EmitInt32 idx
        else
            // Note, we can have idx=0x10000 generated for method table idx + 1 for just beyond last index of method table.
            // This indicates that a MethodList, FieldList, PropertyList or EventList has zero entries
            // For this case, the EmitInt32AsUInt16 writes a 0 (null) into the field.  Binary readers respect this as an empty
            // list of methods/fields/properties/events.
            if idx > 0x10000 then
                System.Diagnostics.Debug.Assert (false, "EmitZUntaggedIndex: too big for small address or simple index")
            buf.EmitInt32AsUInt16 idx

    // Emit compressed tagged integer
    member buf.EmitZTaggedIndex tag nbits big idx =
        let idx2 = (idx <<< nbits) ||| tag
        if big then buf.EmitInt32 idx2
        else buf.EmitInt32AsUInt16 idx2

let getUncodedToken (tab: TableName) idx = ((tab.Index <<< 24) ||| idx)

// From ECMA for UserStrings:
// This final byte holds the value 1 if and only if any UTF16 character within the string has any bit set in its top byte, or its low byte is any of the following:
// 0x01-0x08, 0x0E-0x1F, 0x27, 0x2D,
// 0x7F. Otherwise, it holds 0. The 1 signifies Unicode characters that require handling beyond that normally provided for 8-bit encoding sets.

// HOWEVER, there is a discrepancy here between the ECMA spec and the Microsoft C# implementation.
// The code below follows the latter. We've raised the issue with both teams. See Dev10 bug 850073 for details.

let markerForUnicodeBytes (b: byte[]) =
    let len = b.Length
    let rec scan i =
        i < len/2 &&
        (let b1 = Bytes.get b (i*2)
         let b2 = Bytes.get b (i*2+1)
         (b2 <> 0)
         || (b1 >= 0x01 && b1 <= 0x08)   // as per ECMA and C#
         || (b1 >= 0xE && b1 <= 0x1F)    // as per ECMA and C#
         || (b1 = 0x27)                  // as per ECMA and C#
         || (b1 = 0x2D)                  // as per ECMA and C#
         || (b1 > 0x7F)                  // as per C# (but ECMA omits this)
         || scan (i+1))
    let marker = if scan 0 then 0x01 else 0x00
    marker


// --------------------------------------------------------------------
// Fixups
// --------------------------------------------------------------------

/// Check that the data held at a fixup is some special magic value, as a sanity check
/// to ensure the fixup is being placed at a ood location.
let checkFixup32 (data: byte[]) offset exp =
    if data[offset + 3] <> b3 exp then failwith "fixup sanity check failed"
    if data[offset + 2] <> b2 exp then failwith "fixup sanity check failed"
    if data[offset + 1] <> b1 exp then failwith "fixup sanity check failed"
    if data[offset] <> b0 exp then failwith "fixup sanity check failed"

let applyFixup32 (data: byte[]) offset v =
    data[offset] <- b0 v
    data[offset+1] <- b1 v
    data[offset+2] <- b2 v
    data[offset+3] <- b3 v

//---------------------------------------------------------------------
// TYPES FOR TABLES
//---------------------------------------------------------------------

module RowElementTags =
    let [<Literal>] UShort = 0
    let [<Literal>] ULong = 1
    let [<Literal>] Data = 2
    let [<Literal>] DataResources = 3
    let [<Literal>] Guid = 4
    let [<Literal>] Blob = 5
    let [<Literal>] String = 6
    let [<Literal>] SimpleIndexMin = 7
    let SimpleIndex (t : TableName) = assert (t.Index <= 112); SimpleIndexMin + t.Index
    let [<Literal>] SimpleIndexMax = 119

    let [<Literal>] TypeDefOrRefOrSpecMin = 120
    let TypeDefOrRefOrSpec (t: TypeDefOrRefTag) = assert (t.Tag <= 2); TypeDefOrRefOrSpecMin + t.Tag (* + 111 + 1 = 0x70 + 1 = max TableName.Tndex + 1 *)
    let [<Literal>] TypeDefOrRefOrSpecMax = 122

    let [<Literal>] TypeOrMethodDefMin = 123
    let TypeOrMethodDef (t: TypeOrMethodDefTag) = assert (t.Tag <= 1); TypeOrMethodDefMin + t.Tag (* + 2 + 1 = max TypeDefOrRefOrSpec.Tag + 1 *)
    let [<Literal>] TypeOrMethodDefMax = 124

    let [<Literal>] HasConstantMin = 125
    let HasConstant (t: HasConstantTag) = assert (t.Tag <= 2); HasConstantMin + t.Tag (* + 1 + 1 = max TypeOrMethodDef.Tag + 1 *)
    let [<Literal>] HasConstantMax = 127

    let [<Literal>] HasCustomAttributeMin = 128
    let HasCustomAttribute (t: HasCustomAttributeTag) = assert (t.Tag <= 21); HasCustomAttributeMin + t.Tag (* + 2 + 1 = max HasConstant.Tag + 1 *)
    let [<Literal>] HasCustomAttributeMax = 149

    let [<Literal>] HasFieldMarshalMin = 150
    let HasFieldMarshal (t: HasFieldMarshalTag) = assert (t.Tag <= 1); HasFieldMarshalMin + t.Tag (* + 21 + 1 = max HasCustomAttribute.Tag + 1 *)
    let [<Literal>] HasFieldMarshalMax = 151

    let [<Literal>] HasDeclSecurityMin = 152
    let HasDeclSecurity (t: HasDeclSecurityTag) = assert (t.Tag <= 2); HasDeclSecurityMin + t.Tag (* + 1 + 1 = max HasFieldMarshal.Tag + 1 *)
    let [<Literal>] HasDeclSecurityMax = 154

    let [<Literal>] MemberRefParentMin = 155
    let MemberRefParent (t: MemberRefParentTag) = assert (t.Tag <= 4); MemberRefParentMin + t.Tag (* + 2 + 1 = max HasDeclSecurity.Tag + 1 *)
    let [<Literal>] MemberRefParentMax = 159

    let [<Literal>] HasSemanticsMin = 160
    let HasSemantics (t: HasSemanticsTag) = assert (t.Tag <= 1); HasSemanticsMin + t.Tag (* + 4 + 1 = max MemberRefParent.Tag + 1 *)
    let [<Literal>] HasSemanticsMax = 161

    let [<Literal>] MethodDefOrRefMin = 162
    let MethodDefOrRef (t: MethodDefOrRefTag) = assert (t.Tag <= 2); MethodDefOrRefMin + t.Tag (* + 1 + 1 = max HasSemantics.Tag + 1 *)
    let [<Literal>] MethodDefOrRefMax = 164

    let [<Literal>] MemberForwardedMin = 165
    let MemberForwarded (t: MemberForwardedTag) = assert (t.Tag <= 1); MemberForwardedMin + t.Tag (* + 2 + 1 = max MethodDefOrRef.Tag + 1 *)
    let [<Literal>] MemberForwardedMax = 166

    let [<Literal>] ImplementationMin = 167
    let Implementation (t: ImplementationTag) = assert (t.Tag <= 2); ImplementationMin + t.Tag (* + 1 + 1 = max MemberForwarded.Tag + 1 *)
    let [<Literal>] ImplementationMax = 169

    let [<Literal>] CustomAttributeTypeMin = 170
    let CustomAttributeType (t: CustomAttributeTypeTag) = assert (t.Tag <= 3); CustomAttributeTypeMin + t.Tag (* + 2 + 1 = max Implementation.Tag + 1 *)
    let [<Literal>] CustomAttributeTypeMax = 173

    let [<Literal>] ResolutionScopeMin = 174
    let ResolutionScope (t: ResolutionScopeTag) = assert (t.Tag <= 4); ResolutionScopeMin + t.Tag (* + 3 + 1 = max CustomAttributeType.Tag + 1 *)
    let [<Literal>] ResolutionScopeMax = 178

[<Struct>]
type RowElement(tag: int32, idx: int32) =

    member x.Tag = tag
    member x.Val = idx

// These create RowElements
let UShort (x: uint16) = RowElement(RowElementTags.UShort, int32 x)

let ULong (x: int32) = RowElement(RowElementTags.ULong, x)

/// Index into cenv.data or cenv.resources. Gets fixed up later once we known an overall
/// location for the data section. flag indicates if offset is relative to cenv.resources.
let Data (x: int, k: bool) = RowElement((if k then RowElementTags.DataResources else RowElementTags.Data ), x)

/// pos. in guid array
let Guid (x: int) = RowElement(RowElementTags.Guid, x)

/// pos. in blob array
let Blob (x: int) = RowElement(RowElementTags.Blob, x)

/// pos. in string array
let StringE (x: int) = RowElement(RowElementTags.String, x)

/// pos. in some table
let SimpleIndex (t, x: int) = RowElement(RowElementTags.SimpleIndex t, x)

let TypeDefOrRefOrSpec (t, x: int) = RowElement(RowElementTags.TypeDefOrRefOrSpec t, x)

let TypeOrMethodDef (t, x: int) = RowElement(RowElementTags.TypeOrMethodDef t, x)

let HasConstant (t, x: int) = RowElement(RowElementTags.HasConstant t, x)

let HasCustomAttribute (t, x: int) = RowElement(RowElementTags.HasCustomAttribute t, x)

let HasFieldMarshal (t, x: int) = RowElement(RowElementTags.HasFieldMarshal t, x)

let HasDeclSecurity (t, x: int) = RowElement(RowElementTags.HasDeclSecurity t, x)

let MemberRefParent (t, x: int) = RowElement(RowElementTags.MemberRefParent t, x)

let HasSemantics (t, x: int) = RowElement(RowElementTags.HasSemantics t, x)

let MethodDefOrRef (t, x: int) = RowElement(RowElementTags.MethodDefOrRef t, x)

let MemberForwarded (t, x: int) = RowElement(RowElementTags.MemberForwarded t, x)

let Implementation (t, x: int) = RowElement(RowElementTags.Implementation t, x)

let CustomAttributeType (t, x: int) = RowElement(RowElementTags.CustomAttributeType t, x)

let ResolutionScope (t, x: int) = RowElement(RowElementTags.ResolutionScope t, x)

type BlobIndex = int

type StringIndex = int

let BlobIndex (x: BlobIndex) : int = x

let StringIndex (x: StringIndex) : int = x

let inline combineHash x2 acc = 37 * acc + x2 // (acc <<< 6 + acc >>> 2 + x2 + 0x9e3779b9)

let hashRow (elems: RowElement[]) =
    let mutable acc = 0
    for i in 0 .. elems.Length - 1 do
        acc <- (acc <<< 1) + elems[i].Tag + elems[i].Val + 631
    acc

let equalRows (elems: RowElement[]) (elems2: RowElement[]) =
    if elems.Length <> elems2.Length then false else
    let mutable ok = true
    let n = elems.Length
    let mutable i = 0
    while ok && i < n do
        if elems[i].Tag <> elems2[i].Tag || elems[i].Val <> elems2[i].Val then ok <- false
        i <- i + 1
    ok

type GenericRow = RowElement[]

/// This is the representation of shared rows is used for most shared row types.
/// Rows ILAssemblyRef and ILMethodRef are very common and are given their own
/// representations.
[<Struct; CustomEquality; NoComparison>]
type SharedRow(elems: RowElement[], hashCode: int) =

    member x.GenericRow = elems

    override x.GetHashCode() = hashCode

    override x.Equals(obj: obj) =
        match obj with
        | :? SharedRow as y -> equalRows elems y.GenericRow
        | _ -> false

let SharedRow(elems: RowElement[]) = SharedRow(elems, hashRow elems)

/// Special representation : Note, only hashing by name
let AssemblyRefRow(s1, s2, s3, s4, l1, b1, nameIdx, str2, b2) =
    let hashCode = hash nameIdx
    let genericRow = [| UShort s1; UShort s2; UShort s3; UShort s4; ULong l1; Blob b1; StringE nameIdx; StringE str2; Blob b2 |]
    new SharedRow(genericRow, hashCode)

/// Special representation the computes the hash more efficiently
let MemberRefRow(mrp: RowElement, nmIdx: StringIndex, blobIdx: BlobIndex) =
    let hashCode = combineHash (hash blobIdx) (combineHash (hash nmIdx) (hash mrp))
    let genericRow = [| mrp; StringE nmIdx; Blob blobIdx |]
    new SharedRow(genericRow, hashCode)

/// Unshared rows are used for definitional tables where elements do not need to be made unique
/// e.g. ILMethodDef and ILTypeDef. Most tables are like this. We don't precompute a
/// hash code for these rows, and indeed the GetHashCode and Equals should not be needed.
[<Struct; CustomEquality; NoComparison>]
type UnsharedRow(elems: RowElement[]) =

    member x.GenericRow = elems

    override x.GetHashCode() = hashRow elems

    override x.Equals(obj: obj) =
        match obj with
        | :? UnsharedRow as y -> equalRows elems y.GenericRow
        | _ -> false

//=====================================================================
//=====================================================================
// IL --> TABLES+CODE
//=====================================================================
//=====================================================================

// This environment keeps track of how many generic parameters are in scope.
// This lets us translate AbsIL type variable number to IL type variable numbering
type ILTypeWriterEnv = { EnclosingTyparCount: int }
let envForTypeDef (tdef: ILTypeDef) = { EnclosingTyparCount=tdef.GenericParams.Length }
let envForMethodRef env (ty: ILType) = { EnclosingTyparCount=(match ty with ILType.Array _ -> env.EnclosingTyparCount | _ -> ty.GenericArgs.Length) }
let envForNonGenericMethodRef _mref = { EnclosingTyparCount=Int32.MaxValue }
let envForFieldSpec (fspec: ILFieldSpec) = { EnclosingTyparCount=fspec.DeclaringType.GenericArgs.Length }
let envForOverrideSpec (ospec: ILOverridesSpec) = { EnclosingTyparCount=ospec.DeclaringType.GenericArgs.Length }

//---------------------------------------------------------------------
// TABLES
//---------------------------------------------------------------------

[<NoEquality; NoComparison>]
type MetadataTable<'T
#if !NO_CHECKNULLS
    when 'T:not null
#endif
    > =
    { name: string
      dict: Dictionary<'T, int> // given a row, find its entry number
      mutable rows: ResizeArray<'T> }

    member x.Count = x.rows.Count

    static member New(nm, hashEq) =
        { name=nm
          dict = Dictionary<_, _>(100, hashEq)
          rows= ResizeArray<_>() }

    member tbl.EntriesAsArray =
        tbl.rows |> ResizeArray.toArray

    member tbl.Entries =
        tbl.rows |> ResizeArray.toList

    member tbl.AddSharedEntry x =
        let n = tbl.rows.Count + 1
        tbl.dict[x] <- n
        tbl.rows.Add x
        n

    member tbl.AddUnsharedEntry x =
        let n = tbl.rows.Count + 1
        tbl.rows.Add x
        n

    member tbl.FindOrAddSharedEntry x =
        match tbl.dict.TryGetValue x with
        | true, res -> res
        | _ -> tbl.AddSharedEntry x

    member tbl.Contains x  = tbl.dict.ContainsKey x

    /// This is only used in one special place - see further below.
    member tbl.SetRowsOfTable t =
        tbl.rows <- ResizeArray.ofArray t
        let h = tbl.dict
        h.Clear()
        t |> Array.iteri (fun i x -> h[x] <- (i+1))

    member tbl.AddUniqueEntry nm getter x =
        if tbl.dict.ContainsKey x then failwith ("duplicate entry '"+getter x+"' in "+nm+" table")
        else tbl.AddSharedEntry x

    member tbl.GetTableEntry x = tbl.dict[x]

    override x.ToString() = "table " + x.name

//---------------------------------------------------------------------
// Keys into some of the tables
//---------------------------------------------------------------------

/// We use this key type to help find ILMethodDefs for MethodRefs
type MethodDefKey(ilg:ILGlobals, tidx: int, garity: int, nm: string, retTy: ILType, argTys: ILTypes, isStatic: bool) =
    // Precompute the hash. The hash doesn't include the return type or
    // argument types (only argument type count). This is very important, since
    // hashing these is way too expensive
    let hashCode =
       hash tidx
       |> combineHash (hash garity)
       |> combineHash (hash nm)
       |> combineHash (hash argTys.Length)
       |> combineHash (hash isStatic)

    member _.TypeIdx = tidx

    member _.GenericArity = garity

    member _.Name = nm

    member _.ReturnType = retTy

    member _.ArgTypes = argTys

    member _.IsStatic = isStatic

    override _.GetHashCode() = hashCode

    override _.Equals(obj: obj) =
        match obj with
        | :? MethodDefKey as y ->
            let compareILTypes o1 o2 =
                match o1, o2 with
                | ILType.Value v1, ILType.Value v2 -> v1.EqualsWithPrimaryScopeRef(ilg.primaryAssemblyScopeRef, v2 :> obj )
                | _ -> o1 = o2

            tidx = y.TypeIdx &&
            garity = y.GenericArity &&
            nm = y.Name &&
            // note: these next two use structural equality on AbstractIL ILType values
            retTy = y.ReturnType && List.lengthsEqAndForall2 compareILTypes argTys y.ArgTypes &&
            isStatic = y.IsStatic
        | _ -> false

    override x.ToString() = nm

/// We use this key type to help find ILFieldDefs for FieldRefs
type FieldDefKey(tidx: int, nm: string, ty: ILType) =
    // precompute the hash. hash doesn't include the type
    let hashCode = hash tidx |> combineHash (hash nm)

    member _.TypeIdx = tidx

    member _.Name = nm

    member _.Type = ty

    override _.GetHashCode() = hashCode

    override _.Equals(obj: obj) =
        match obj with
        | :? FieldDefKey as y ->
            tidx = y.TypeIdx &&
            nm = y.Name &&
            ty = y.Type
        | _ -> false

type PropertyTableKey = PropKey of int (* type. def. idx. *) * string * ILType * ILTypes

type EventTableKey = EventKey of int (* type. def. idx. *) * string

type TypeDefTableKey = TdKey of string list (* enclosing *) * string (* type name *)

//---------------------------------------------------------------------
// The Writer Context
//---------------------------------------------------------------------

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type MetadataTable =
    | Shared of MetadataTable<SharedRow>
    | Unshared of MetadataTable<UnsharedRow>
    member t.FindOrAddSharedEntry x = match t with Shared u -> u.FindOrAddSharedEntry x | Unshared u -> failwithf "FindOrAddSharedEntry: incorrect table kind, u.name = %s" u.name
    member t.AddSharedEntry x = match t with | Shared u -> u.AddSharedEntry x | Unshared u -> failwithf "AddSharedEntry: incorrect table kind, u.name = %s" u.name
    member t.AddUnsharedEntry x = match t with Unshared u -> u.AddUnsharedEntry x | Shared u -> failwithf "AddUnsharedEntry: incorrect table kind, u.name = %s" u.name
    member t.GenericRowsOfTable = match t with Unshared u -> u.EntriesAsArray |> Array.map (fun x -> x.GenericRow) | Shared u -> u.EntriesAsArray |> Array.map (fun x -> x.GenericRow)
    member t.SetRowsOfSharedTable rows = match t with Shared u -> u.SetRowsOfTable (Array.map SharedRow rows) | Unshared u -> failwithf "SetRowsOfSharedTable: incorrect table kind, u.name = %s" u.name
    member t.Count = match t with Unshared u -> u.Count | Shared u -> u.Count


[<NoEquality; NoComparison>]
type cenv =
    { ilg: ILGlobals

      emitTailcalls: bool

      deterministic: bool    

      desiredMetadataVersion: ILVersionInfo

      requiredDataFixups: (int32 * (int * bool)) list ref

      /// References to strings in codestreams: offset of code and a (fixup-location, string token) list)
      mutable requiredStringFixups: (int32 * (int * int) list) list

      codeChunks: ByteBuffer

      mutable nextCodeAddr: int32

      /// Collected debug information
      mutable moduleGuid: byte[]

      generatePdb: bool

      pdbinfo: ResizeArray<PdbMethodData>

      documents: MetadataTable<PdbDocumentData>

      /// Raw data, to go into the data section
      data: ByteBuffer

      /// Raw resource data, to go into the data section
      resources: ByteBuffer

      mutable entrypoint: (bool * int) option

      /// Caches
      trefCache: Dictionary<ILTypeRef, int>

      /// The following are all used to generate unique items in the output
      tables: MetadataTable[]

      AssemblyRefs: MetadataTable<SharedRow>

      fieldDefs: MetadataTable<FieldDefKey>

      methodDefIdxsByKey: MetadataTable<MethodDefKey>

      methodDefIdxs: Dictionary<ILMethodDef, int>

      implementsIdxs: Dictionary<int,int list>

      propertyDefs: MetadataTable<PropertyTableKey>

      eventDefs: MetadataTable<EventTableKey>

      typeDefs: MetadataTable<TypeDefTableKey>

      guids: MetadataTable<byte[]>

      blobs: MetadataTable<byte[]>

      strings: MetadataTable<string>

      userStrings: MetadataTable<string>

      normalizeAssemblyRefs: ILAssemblyRef -> ILAssemblyRef

      /// Indicates that the writing assembly will have an assembly-level attribute, System.Runtime.CompilerServices.InternalsVisibleToAttribute.
      hasInternalsVisibleToAttrib: bool

      /// Indicates that the writing assembly will be a reference assembly. Method bodies will be replaced with a `throw null` if there are any.
      referenceAssemblyOnly: bool

      pdbImports: Dictionary<ILDebugImports, PdbImports>
    }
    member cenv.GetTable (tab: TableName) = cenv.tables[tab.Index]

    member cenv.AddCode ((reqdStringFixupsOffset, requiredStringFixups), code) =
        if align 4 cenv.nextCodeAddr <> cenv.nextCodeAddr then dprintn "warning: code not 4-byte aligned"
        cenv.requiredStringFixups <- (cenv.nextCodeAddr + reqdStringFixupsOffset, requiredStringFixups) :: cenv.requiredStringFixups
        cenv.codeChunks.EmitBytes code
        cenv.nextCodeAddr <- cenv.nextCodeAddr + code.Length

    member cenv.GetCode() = cenv.codeChunks.AsMemory().ToArray()

    member cenv.EmitDebugDocument (doc: ILSourceDocument) =
        if cenv.generatePdb then
          cenv.documents.FindOrAddSharedEntry doc |> ignore

    override x.ToString() = "<cenv>"

    interface IDisposable with
        member this.Dispose() =
            (this.codeChunks :> IDisposable).Dispose()
            (this.data :> IDisposable).Dispose()
            (this.resources :> IDisposable).Dispose()

let FindOrAddSharedRow (cenv: cenv) tbl x = cenv.GetTable(tbl).FindOrAddSharedEntry x

// Shared rows must be hash-cons'd to be made unique (no duplicates according to contents)
let AddSharedRow (cenv: cenv) tbl x = cenv.GetTable(tbl).AddSharedEntry x

// Unshared rows correspond to definition elements (e.g. a ILTypeDef or a ILMethodDef)
let AddUnsharedRow (cenv: cenv) tbl (x: UnsharedRow) = cenv.GetTable(tbl).AddUnsharedEntry x

let metadataSchemaVersionSupportedByCLRVersion v =
    // Whidbey Beta 1 version numbers are between 2.0.40520.0 and 2.0.40607.0
    // Later Whidbey versions are post 2.0.40607.0.. However we assume
    // internal builds such as 2.0.x86chk are Whidbey Beta 2 or later
    if compareILVersions v (parseILVersion "2.0.40520.0") >= 0 &&
       compareILVersions v (parseILVersion "2.0.40608.0") < 0 then 1, 1
    elif compareILVersions v (parseILVersion "2.0.0.0") >= 0 then 2, 0
    else 1, 0

let headerVersionSupportedByCLRVersion v =
   // The COM20HEADER version number
   // Whidbey version numbers are 2.5
   // Earlier are 2.0
   // From an email from jeffschw: "Be built with a compiler that marks the COM20HEADER with Major >=2 and Minor >= 5. The V2.0 compilers produce images with 2.5, V1.x produces images with 2.0."
    if compareILVersions v (parseILVersion "2.0.0.0") >= 0 then 2, 5
    else 2, 0

let peOptionalHeaderByteByCLRVersion v =
   //  A flag in the PE file optional header seems to depend on CLI version
   // Whidbey version numbers are 8
   // Earlier are 6
   // Tools are meant to ignore this, but the VS Profiler wants it to have the right value
    if compareILVersions v (parseILVersion "2.0.0.0") >= 0 then 8
    else 6

// returned by writeBinary
[<NoEquality; NoComparison>]
type ILTokenMappings =
    { TypeDefTokenMap: ILTypeDef list * ILTypeDef -> int32
      FieldDefTokenMap: ILTypeDef list * ILTypeDef -> ILFieldDef -> int32
      MethodDefTokenMap: ILTypeDef list * ILTypeDef -> ILMethodDef -> int32
      PropertyTokenMap: ILTypeDef list * ILTypeDef -> ILPropertyDef -> int32
      EventTokenMap: ILTypeDef list * ILTypeDef -> ILEventDef -> int32 }

let recordRequiredDataFixup (requiredDataFixups: ('T * 'U) list ref) (buf: ByteBuffer) pos lab =
    requiredDataFixups.Value <- (pos, lab) :: requiredDataFixups.Value
    // Write a special value in that we check later when applying the fixup
    buf.EmitInt32 0xdeaddddd

//---------------------------------------------------------------------
// The UserString, BlobHeap, GuidHeap tables
//---------------------------------------------------------------------

let GetUserStringHeapIdx cenv s =
    cenv.userStrings.FindOrAddSharedEntry s

let GetBytesAsBlobIdx cenv (bytes: byte[]) =
    if bytes.Length = 0 then 0
    else cenv.blobs.FindOrAddSharedEntry bytes

let GetStringHeapIdx cenv s =
    if String.IsNullOrEmpty(s) then 0
    else cenv.strings.FindOrAddSharedEntry s

let GetGuidIdx cenv info = cenv.guids.FindOrAddSharedEntry info

let GetStringHeapIdxOption cenv sopt =
    match sopt with
    | Some ns -> GetStringHeapIdx cenv ns
    | None -> 0

let GetTypeNameAsElemPair cenv n =
    let n1, n2 = splitTypeNameRight n
    StringE (GetStringHeapIdxOption cenv n1),
    StringE (GetStringHeapIdx cenv n2)

//=====================================================================
// Pass 1 - allocate indexes for types
//=====================================================================

let rec GenTypeDefPass1 enc cenv (tdef: ILTypeDef) =
    ignore (cenv.typeDefs.AddUniqueEntry "type index" (fun (TdKey (_, n)) -> n) (TdKey (enc, tdef.Name)))
 
    // Verify that the typedef contains fewer than maximumMethodsPerDotNetType
    let count = tdef.Methods.AsArray().Length
    if count > maximumMethodsPerDotNetType then
        errorR(Error(FSComp.SR.tooManyMethodsInDotNetTypeWritingAssembly (tdef.Name, count, maximumMethodsPerDotNetType), rangeStartup))

    GenTypeDefsPass1 (enc@[tdef.Name]) cenv (tdef.NestedTypes.AsList())

and GenTypeDefsPass1 enc cenv tdefs = List.iter (GenTypeDefPass1 enc cenv) tdefs

//=====================================================================
// Pass 2 - allocate indexes for methods and fields and write rows for types
//=====================================================================

let rec GetIdxForTypeDef cenv key =
    try
        cenv.typeDefs.GetTableEntry key
    with
      :? KeyNotFoundException ->
        let (TdKey (enc, n) ) = key
        errorR(InternalError("One of your modules expects the type '"+String.concat "." (enc@[n])+"' to be defined within the module being emitted. You may be missing an input file", range0))
        0

// --------------------------------------------------------------------
// Assembly and module references
// --------------------------------------------------------------------

let rec GetAssemblyRefAsRow cenv (aref: ILAssemblyRef) =
    AssemblyRefRow
        ((match aref.Version with None -> 0us | Some version -> version.Major),
         (match aref.Version with None -> 0us | Some version -> version.Minor),
         (match aref.Version with None -> 0us | Some version -> version.Build),
         (match aref.Version with None -> 0us | Some version -> version.Revision),
         ((match aref.PublicKey with Some (PublicKey _) -> 0x0001 | _ -> 0x0000)
          ||| (if aref.Retargetable then 0x0100 else 0x0000)),
         BlobIndex (match aref.PublicKey with
                    | None -> 0
                    | Some (PublicKey b | PublicKeyToken b) -> GetBytesAsBlobIdx cenv b),
         StringIndex (GetStringHeapIdx cenv aref.Name),
         StringIndex (match aref.Locale with None -> 0 | Some s -> GetStringHeapIdx cenv s),
         BlobIndex (match aref.Hash with None -> 0 | Some s -> GetBytesAsBlobIdx cenv s))

and GetAssemblyRefAsIdx cenv aref =
    FindOrAddSharedRow cenv TableNames.AssemblyRef (GetAssemblyRefAsRow cenv (cenv.normalizeAssemblyRefs aref))

and GetModuleRefAsRow cenv (mref: ILModuleRef) =
    SharedRow
        [| StringE (GetStringHeapIdx cenv mref.Name) |]

and GetModuleRefAsFileRow cenv (mref: ILModuleRef) =
    SharedRow
        [| ULong (if mref.HasMetadata then 0x0000 else 0x0001)
           StringE (GetStringHeapIdx cenv mref.Name)
           (match mref.Hash with None -> Blob 0 | Some s -> Blob (GetBytesAsBlobIdx cenv s)) |]

and GetModuleRefAsIdx cenv mref =
    FindOrAddSharedRow cenv TableNames.ModuleRef (GetModuleRefAsRow cenv mref)

and GetModuleRefAsFileIdx cenv mref =
    FindOrAddSharedRow cenv TableNames.File (GetModuleRefAsFileRow cenv mref)

// --------------------------------------------------------------------
// Does a ILScopeRef point to this module?
// --------------------------------------------------------------------

let isScopeRefLocal scoref = (scoref = ILScopeRef.Local)
let isTypeRefLocal (tref: ILTypeRef) = isScopeRefLocal tref.Scope
let isTypeLocal (ty: ILType) = ty.IsNominal && isNil ty.GenericArgs && isTypeRefLocal ty.TypeRef

// --------------------------------------------------------------------
// Scopes to Implementation elements.
// --------------------------------------------------------------------

let GetScopeRefAsImplementationElem cenv scoref =
    match scoref with
    | ILScopeRef.Local -> (i_AssemblyRef, 0)
    | ILScopeRef.Assembly aref -> (i_AssemblyRef, GetAssemblyRefAsIdx cenv aref)
    | ILScopeRef.Module mref -> (i_File, GetModuleRefAsFileIdx cenv mref)
    | ILScopeRef.PrimaryAssembly -> (i_AssemblyRef, GetAssemblyRefAsIdx cenv cenv.ilg.primaryAssemblyRef)

// --------------------------------------------------------------------
// Type references, types etc.
// --------------------------------------------------------------------

let rec GetTypeRefAsTypeRefRow cenv (tref: ILTypeRef) =
    let nselem, nelem = GetTypeNameAsElemPair cenv tref.Name
    let rs1, rs2 = GetResolutionScopeAsElem cenv (tref.Scope, tref.Enclosing)
    SharedRow [| ResolutionScope (rs1, rs2); nelem; nselem |]

and GetTypeRefAsTypeRefIdx cenv tref =
    match cenv.trefCache.TryGetValue tref with
    | true, res -> res
    | _ ->
        let res = FindOrAddSharedRow cenv TableNames.TypeRef (GetTypeRefAsTypeRefRow cenv tref)
        cenv.trefCache[tref] <- res
        res

and GetTypeDescAsTypeRefIdx cenv (scoref, enc, n) =
    GetTypeRefAsTypeRefIdx cenv (mkILNestedTyRef (scoref, enc, n))

and GetResolutionScopeAsElem cenv (scoref, enc) =
    match List.tryFrontAndBack enc with
    | None ->
        match scoref with
        | ILScopeRef.Local -> (rs_Module, 1)
        | ILScopeRef.Assembly aref -> (rs_AssemblyRef, GetAssemblyRefAsIdx cenv aref)
        | ILScopeRef.Module mref -> (rs_ModuleRef, GetModuleRefAsIdx cenv mref)
        | ILScopeRef.PrimaryAssembly -> (rs_AssemblyRef, GetAssemblyRefAsIdx cenv cenv.ilg.primaryAssemblyRef)
    | Some (enc2, n2) ->
        (rs_TypeRef, GetTypeDescAsTypeRefIdx cenv (scoref, enc2, n2))

let getTypeInfoAsTypeDefOrRefEncoded cenv (scoref, enc, nm) =
    if isScopeRefLocal scoref then
        let idx = GetIdxForTypeDef cenv (TdKey(enc, nm))
        idx <<< 2 // ECMA 22.2.8 TypeDefOrRefEncoded - ILTypeDef
    else
        let idx = GetTypeDescAsTypeRefIdx cenv (scoref, enc, nm)
        ((idx <<< 2) ||| 0x01) // ECMA 22.2.8 TypeDefOrRefEncoded - ILTypeRef

let emitTypeInfoAsTypeDefOrRefEncoded cenv (bb: ByteBuffer) (scoref, enc, nm) =
    let tok = getTypeInfoAsTypeDefOrRefEncoded cenv (scoref, enc, nm)
    bb.EmitZ32 tok

let getTypeDefOrRefAsUncodedToken (tag, idx) =
    let tab =
        if tag = tdor_TypeDef then TableNames.TypeDef
        elif tag = tdor_TypeRef then TableNames.TypeRef
        elif tag = tdor_TypeSpec then TableNames.TypeSpec
        else failwith "getTypeDefOrRefAsUncodedToken"
    getUncodedToken tab idx

// REVIEW: write into an accumulating buffer
let EmitArrayShape (bb: ByteBuffer) (ILArrayShape shape) =
    let sized = List.filter (function _, Some _ -> true | _ -> false) shape
    let lobounded = List.filter (function Some _, _ -> true | _ -> false) shape
    bb.EmitZ32 shape.Length
    bb.EmitZ32 sized.Length
    sized |> List.iter (function _, Some sz -> bb.EmitZ32 sz | _ -> failwith "?")
    bb.EmitZ32 lobounded.Length
    lobounded |> List.iter (function Some low, _ -> bb.EmitZ32 low | _ -> failwith "?")

let hasthisToByte hasthis =
     match hasthis with
     | ILThisConvention.Instance -> e_IMAGE_CEE_CS_CALLCONV_INSTANCE
     | ILThisConvention.InstanceExplicit -> e_IMAGE_CEE_CS_CALLCONV_INSTANCE_EXPLICIT
     | ILThisConvention.Static -> 0x00uy

let callconvToByte ntypars (Callconv (hasthis, bcc)) =
    hasthisToByte hasthis |||
    (if ntypars > 0 then e_IMAGE_CEE_CS_CALLCONV_GENERIC else 0x00uy) |||
    (match bcc with
    | ILArgConvention.FastCall -> e_IMAGE_CEE_CS_CALLCONV_FASTCALL
    | ILArgConvention.StdCall -> e_IMAGE_CEE_CS_CALLCONV_STDCALL
    | ILArgConvention.ThisCall -> e_IMAGE_CEE_CS_CALLCONV_THISCALL
    | ILArgConvention.CDecl -> e_IMAGE_CEE_CS_CALLCONV_CDECL
    | ILArgConvention.Default -> 0x00uy
    | ILArgConvention.VarArg -> e_IMAGE_CEE_CS_CALLCONV_VARARG)


// REVIEW: write into an accumulating buffer
let rec EmitTypeSpec cenv env (bb: ByteBuffer) (et, tspec: ILTypeSpec) =
    if isNil tspec.GenericArgs then
        bb.EmitByte et
        emitTypeInfoAsTypeDefOrRefEncoded cenv bb (tspec.Scope, tspec.Enclosing, tspec.Name)
    else
        bb.EmitByte et_WITH
        bb.EmitByte et
        emitTypeInfoAsTypeDefOrRefEncoded cenv bb (tspec.Scope, tspec.Enclosing, tspec.Name)
        bb.EmitZ32 tspec.GenericArgs.Length
        EmitTypes cenv env bb tspec.GenericArgs

and GetTypeAsTypeDefOrRef cenv env (ty: ILType) =
    if isTypeLocal ty then
        let tref = ty.TypeRef
        (tdor_TypeDef, GetIdxForTypeDef cenv (TdKey(tref.Enclosing, tref.Name)))
    elif ty.IsNominal && isNil ty.GenericArgs then
        (tdor_TypeRef, GetTypeRefAsTypeRefIdx cenv ty.TypeRef)
    else
        (tdor_TypeSpec, GetTypeAsTypeSpecIdx cenv env ty)

and GetTypeAsBytes cenv env ty = emitBytesViaBuffer (fun bb -> EmitType cenv env bb ty)

and GetTypeOfLocalAsBytes cenv env (l: ILLocal) =
    emitBytesViaBuffer (fun bb -> EmitLocalInfo cenv env bb l)

and GetTypeAsBlobIdx cenv env (ty: ILType) =
    GetBytesAsBlobIdx cenv (GetTypeAsBytes cenv env ty)

and GetTypeAsTypeSpecRow cenv env (ty: ILType) =
    SharedRow [| Blob (GetTypeAsBlobIdx cenv env ty) |]

and GetTypeAsTypeSpecIdx cenv env ty =
    FindOrAddSharedRow cenv TableNames.TypeSpec (GetTypeAsTypeSpecRow cenv env ty)

and EmitType cenv env bb ty =
    let ilg = cenv.ilg
    match ty with
    | ty when isILSByteTy ilg ty -> bb.EmitByte et_I1
    | ty when isILInt16Ty ilg ty -> bb.EmitByte et_I2
    | ty when isILInt32Ty ilg ty -> bb.EmitByte et_I4
    | ty when isILInt64Ty ilg ty -> bb.EmitByte et_I8
    | ty when isILByteTy ilg ty -> bb.EmitByte et_U1
    | ty when isILUInt16Ty ilg ty -> bb.EmitByte et_U2
    | ty when isILUInt32Ty ilg ty -> bb.EmitByte et_U4
    | ty when isILUInt64Ty ilg ty -> bb.EmitByte et_U8
    | ty when isILDoubleTy ilg ty -> bb.EmitByte et_R8
    | ty when isILSingleTy ilg ty -> bb.EmitByte et_R4
    | ty when isILBoolTy ilg ty -> bb.EmitByte et_BOOLEAN
    | ty when isILCharTy ilg ty -> bb.EmitByte et_CHAR
    | ty when isILStringTy ilg ty -> bb.EmitByte et_STRING
    | ty when isILObjectTy ilg ty -> bb.EmitByte et_OBJECT
    | ty when isILIntPtrTy ilg ty -> bb.EmitByte et_I
    | ty when isILUIntPtrTy ilg ty -> bb.EmitByte et_U
    | ty when isILTypedReferenceTy ilg ty -> bb.EmitByte et_TYPEDBYREF

    | ILType.Boxed tspec -> EmitTypeSpec cenv env bb (et_CLASS, tspec)
    | ILType.Value tspec -> EmitTypeSpec cenv env bb (et_VALUETYPE, tspec)
    | ILType.Array (shape, ty) ->
        if shape = ILArrayShape.SingleDimensional then (bb.EmitByte et_SZARRAY ; EmitType cenv env bb ty)
        else (bb.EmitByte et_ARRAY; EmitType cenv env bb ty; EmitArrayShape bb shape)
    | ILType.TypeVar tv ->
        let cgparams = env.EnclosingTyparCount
        if int32 tv < cgparams then
            bb.EmitByte et_VAR
            bb.EmitZ32 (int32 tv)
        else
            bb.EmitByte et_MVAR
            bb.EmitZ32 (int32 tv - cgparams)

    | ILType.Byref ty ->
        bb.EmitByte et_BYREF
        EmitType cenv env bb ty
    | ILType.Ptr ty ->
        bb.EmitByte et_PTR
        EmitType cenv env bb ty
    | ILType.Void ->
        bb.EmitByte et_VOID
    | ILType.FunctionPointer x ->
        bb.EmitByte et_FNPTR
        EmitCallsig cenv env bb (x.CallingConv, x.ArgTypes, x.ReturnType, None, 0)
    | ILType.Modified (req, tref, ty) ->
        bb.EmitByte (if req then et_CMOD_REQD else et_CMOD_OPT)
        emitTypeInfoAsTypeDefOrRefEncoded cenv bb (tref.Scope, tref.Enclosing, tref.Name)
        EmitType cenv env bb ty

and EmitLocalInfo cenv env (bb: ByteBuffer) (l: ILLocal) =
    if l.IsPinned then
        bb.EmitByte et_PINNED
    EmitType cenv env bb l.Type

and EmitCallsig cenv env bb (callconv, args: ILTypes, ret, varargs: ILVarArgs, genarity) =
    bb.EmitByte (callconvToByte genarity callconv)
    if genarity > 0 then bb.EmitZ32 genarity
    bb.EmitZ32 (args.Length + (match varargs with None -> 0 | Some l -> l.Length))
    EmitType cenv env bb ret
    args |> List.iter (EmitType cenv env bb)
    match varargs with
     | None -> ()// no extra arg = no sentinel
     | Some tys ->
         if isNil tys then () // no extra arg = no sentinel
         else
            bb.EmitByte et_SENTINEL
            List.iter (EmitType cenv env bb) tys

and GetCallsigAsBytes cenv env x = emitBytesViaBuffer (fun bb -> EmitCallsig cenv env bb x)

// REVIEW: write into an accumulating buffer
and EmitTypes cenv env bb (inst: ILTypes) =
    inst |> List.iter (EmitType cenv env bb)

let GetTypeAsMemberRefParent cenv env ty =
    match GetTypeAsTypeDefOrRef cenv env ty with
    | tag, _ when tag = tdor_TypeDef -> dprintn "GetTypeAsMemberRefParent: mspec should have been encoded as mdtMethodDef?"; MemberRefParent (mrp_TypeRef, 1)
    | tag, tok when tag = tdor_TypeRef -> MemberRefParent (mrp_TypeRef, tok)
    | tag, tok when tag = tdor_TypeSpec -> MemberRefParent (mrp_TypeSpec, tok)
    | _ -> failwith "GetTypeAsMemberRefParent"


// --------------------------------------------------------------------
// Native types
// --------------------------------------------------------------------

let rec GetVariantTypeAsInt32 ty =
    if List.memAssoc ty (Lazy.force ILVariantTypeMap) then
        (List.assoc ty (Lazy.force ILVariantTypeMap ))
    else
        match ty with
        | ILNativeVariant.Array vt -> vt_ARRAY ||| GetVariantTypeAsInt32 vt
        | ILNativeVariant.Vector vt -> vt_VECTOR ||| GetVariantTypeAsInt32 vt
        | ILNativeVariant.Byref vt -> vt_BYREF ||| GetVariantTypeAsInt32 vt
        | _ -> failwith "Unexpected variant type"

// based on information in ECMA and asmparse.y in the CLR codebase
let rec GetNativeTypeAsBlobIdx cenv (ty: ILNativeType) =
    GetBytesAsBlobIdx cenv (GetNativeTypeAsBytes ty)

and GetNativeTypeAsBytes ty = emitBytesViaBuffer (fun bb -> EmitNativeType bb ty)

// REVIEW: write into an accumulating buffer
and EmitNativeType bb ty =
    if List.memAssoc ty (Lazy.force ILNativeTypeRevMap) then
        bb.EmitByte (List.assoc ty (Lazy.force ILNativeTypeRevMap))
    else
      match ty with
      | ILNativeType.Empty -> ()
      | ILNativeType.Custom (guid, nativeTypeName, custMarshallerName, cookieString) ->
          let u1 = System.Text.Encoding.UTF8.GetBytes nativeTypeName
          let u2 = System.Text.Encoding.UTF8.GetBytes custMarshallerName
          let u3 = cookieString
          bb.EmitByte nt_CUSTOMMARSHALER
          bb.EmitZ32 guid.Length
          bb.EmitBytes guid
          bb.EmitZ32 u1.Length; bb.EmitBytes u1
          bb.EmitZ32 u2.Length; bb.EmitBytes u2
          bb.EmitZ32 u3.Length; bb.EmitBytes u3
      | ILNativeType.FixedSysString i ->
          bb.EmitByte nt_FIXEDSYSSTRING
          bb.EmitZ32 i

      | ILNativeType.FixedArray i ->
          bb.EmitByte nt_FIXEDARRAY
          bb.EmitZ32 i
      | (* COM interop *) ILNativeType.SafeArray (vt, name) ->
          bb.EmitByte nt_SAFEARRAY
          bb.EmitZ32 (GetVariantTypeAsInt32 vt)
          match name with
          | None -> ()
          | Some n ->
               let u1 = Bytes.stringAsUtf8NullTerminated n
               bb.EmitZ32 (Array.length u1) ; bb.EmitBytes u1
      | ILNativeType.Array (nt, sizeinfo) -> (* REVIEW: check if this corresponds to the ECMA spec *)
          bb.EmitByte nt_ARRAY
          match nt with
          | None -> bb.EmitZ32 (int nt_MAX)
          | Some ntt ->
             (if ntt = ILNativeType.Empty then
               bb.EmitZ32 (int nt_MAX)
              else
                EmitNativeType bb ntt)
          match sizeinfo with
          | None -> ()  // chunk out with zeroes because some tools (e.g. asmmeta) read these poorly and expect further elements.
          | Some (pnum, additive) ->
              // ParamNum
              bb.EmitZ32 pnum
            (* ElemMul *) (* z_u32 0x1l *)
              match additive with
              | None -> ()
              | Some n -> (* NumElem *) bb.EmitZ32 n
      | _ -> failwith "Unexpected native type"

// --------------------------------------------------------------------
// Native types
// --------------------------------------------------------------------

let rec GetFieldInitAsBlobIdx cenv (x: ILFieldInit) =
    GetBytesAsBlobIdx cenv (emitBytesViaBuffer (fun bb -> GetFieldInit bb x))

// REVIEW: write into an accumulating buffer
and GetFieldInit (bb: ByteBuffer) x =
    match x with
    | ILFieldInit.String b -> bb.EmitBytes (System.Text.Encoding.Unicode.GetBytes b)
    | ILFieldInit.Bool b -> bb.EmitByte (if b then 0x01uy else 0x00uy)
    | ILFieldInit.Char x -> bb.EmitUInt16 x
    | ILFieldInit.Int8 x -> bb.EmitByte (byte x)
    | ILFieldInit.Int16 x -> bb.EmitUInt16 (uint16 x)
    | ILFieldInit.Int32 x -> bb.EmitInt32 x
    | ILFieldInit.Int64 x -> bb.EmitInt64 x
    | ILFieldInit.UInt8 x -> bb.EmitByte x
    | ILFieldInit.UInt16 x -> bb.EmitUInt16 x
    | ILFieldInit.UInt32 x -> bb.EmitInt32 (int32 x)
    | ILFieldInit.UInt64 x -> bb.EmitInt64 (int64 x)
    | ILFieldInit.Single x -> bb.EmitInt32 (bitsOfSingle x)
    | ILFieldInit.Double x -> bb.EmitInt64 (bitsOfDouble x)
    | ILFieldInit.Null -> bb.EmitInt32 0

and GetFieldInitFlags i =
    UShort
      (uint16
        (match i with
         | ILFieldInit.String _ -> et_STRING
         | ILFieldInit.Bool _ -> et_BOOLEAN
         | ILFieldInit.Char _ -> et_CHAR
         | ILFieldInit.Int8 _ -> et_I1
         | ILFieldInit.Int16 _ -> et_I2
         | ILFieldInit.Int32 _ -> et_I4
         | ILFieldInit.Int64 _ -> et_I8
         | ILFieldInit.UInt8 _ -> et_U1
         | ILFieldInit.UInt16 _ -> et_U2
         | ILFieldInit.UInt32 _ -> et_U4
         | ILFieldInit.UInt64 _ -> et_U8
         | ILFieldInit.Single _ -> et_R4
         | ILFieldInit.Double _ -> et_R8
         | ILFieldInit.Null -> et_CLASS))

// --------------------------------------------------------------------
// Type definitions
// --------------------------------------------------------------------

let GetMemberAccessFlags access =
    match access with
    | ILMemberAccess.Public -> 0x00000006
    | ILMemberAccess.Private -> 0x00000001
    | ILMemberAccess.Family -> 0x00000004
    | ILMemberAccess.CompilerControlled -> 0x00000000
    | ILMemberAccess.FamilyAndAssembly -> 0x00000002
    | ILMemberAccess.FamilyOrAssembly -> 0x00000005
    | ILMemberAccess.Assembly -> 0x00000003

exception MethodDefNotFound

let private MethodDefIdxExists cenv (mref: ILMethodRef) = 
    let tref = mref.DeclaringTypeRef
    if not (isTypeRefLocal tref) then
        // Method referred to by method impl, event or property is not in a type defined in this module.
        false
    else
        let tidx = GetIdxForTypeDef cenv (TdKey(tref.Enclosing, tref.Name))
        let mdkey = MethodDefKey (cenv.ilg, tidx, mref.GenericArity, mref.Name, mref.ReturnType, mref.ArgTypes, mref.CallingConv.IsStatic)
        cenv.methodDefIdxsByKey.Contains mdkey

let FindMethodDefIdx cenv mdkey =
    try cenv.methodDefIdxsByKey.GetTableEntry mdkey
    with :? KeyNotFoundException ->
      let typeNameOfIdx i =
        match
           (cenv.typeDefs.dict
             |> Seq.fold (fun sofar kvp ->
                let tkey2 = kvp.Key
                let tidx2 = kvp.Value
                if i = tidx2 then
                    if sofar = None then
                        Some tkey2
                    else failwith "multiple type names map to index"
                else sofar) None) with
          | Some x -> x
          | None -> raise MethodDefNotFound
      let (TdKey (tenc, tname)) = typeNameOfIdx mdkey.TypeIdx
      dprintn ("The local method '"+(String.concat "." (tenc@[tname]))+"'::'"+mdkey.Name+"' was referenced but not declared")
      dprintn ("generic arity: "+string mdkey.GenericArity)
      cenv.methodDefIdxsByKey.dict |> Seq.iter (fun (KeyValue(mdkey2, _)) ->
          if mdkey2.TypeIdx = mdkey.TypeIdx && mdkey.Name = mdkey2.Name then
              let (TdKey (tenc2, tname2)) = typeNameOfIdx mdkey2.TypeIdx
              dprintn ("A method in '"+(String.concat "." (tenc2@[tname2]))+"' had the right name but the wrong signature:")
              dprintn ("generic arity: "+string mdkey2.GenericArity)
              dprintn (sprintf "mdkey2: %+A" mdkey2))
      raise MethodDefNotFound

// --------------------------------------------------------------------
// ILMethodRef --> ILMethodDef.
//
// Only successfully converts ILMethodRef's referring to
// methods in the module being emitted.
// --------------------------------------------------------------------
let TryGetMethodRefAsMethodDefIdx cenv (mref: ILMethodRef) =
    let tref = mref.DeclaringTypeRef
    try
        if not (isTypeRefLocal tref) then
             Result.Error $"method referred to by method impl, event or property is not in a type defined in this module, method ref is %A{mref}"
        else
            let tidx = GetIdxForTypeDef cenv (TdKey(tref.Enclosing, tref.Name))
            let mdkey = MethodDefKey (cenv.ilg, tidx, mref.GenericArity, mref.Name, mref.ReturnType, mref.ArgTypes, mref.CallingConv.IsStatic)
            let idx = FindMethodDefIdx cenv mdkey
            Ok idx
    with exn ->
        Result.Error $"Error in GetMethodRefAsMethodDefIdx for mref = %A{(mref.Name, tref.Name)}, error: %s{exn.Message}"

let canGenMethodDef (tdef: ILTypeDef) cenv (mdef: ILMethodDef) =
    if not cenv.referenceAssemblyOnly then
        true
    // If the method is part of attribute type, generate get_* and set_* methods and .ctors for it, consider the following case:
    //      [<AttributeUsage(AttributeTargets.All)>]
    //      type PublicWithInternalSetterPropertyAttribute() =
    //          inherit Attribute()
    //          member val internal Prop1 : int = 0 with get, set
    //      [<PublicWithInternalSetterPropertyAttribute(Prop1=4)>]
    //      type ClassPublicWithAttributes() = class end

    // We want to generate pretty much everything for attributes, because of serialization scenarios, and the fact that non-visible constructors, properties and fields can still be part of reference assembly.
    // Example: NoDynamicInvocationAttribute has an internal constructor, which should be included in the reference assembly.
    else if tdef.IsKnownToBeAttribute && mdef.IsSpecialName && (not mdef.IsClassInitializer) then
        true
    else
        match mdef.Access with
        | ILMemberAccess.Public -> true
        // When emitting a reference assembly, do not emit methods that are private/protected/internal unless they are virtual/abstract or provide an explicit interface implementation.
        // REVIEW: Added(vlza, fixes #14937):
        //   We also emit methods that are marked as HideBySig and static,
        //   since they're not virtual or abstract, but we want (?) the same behaviour as normal instance implementations.
        | ILMemberAccess.Private | ILMemberAccess.Family | ILMemberAccess.Assembly | ILMemberAccess.FamilyOrAssembly
            when (mdef.IsHideBySig && mdef.IsStatic) || mdef.IsVirtual || mdef.IsAbstract || mdef.IsNewSlot || mdef.IsFinal || mdef.IsEntryPoint -> true
        // When emitting a reference assembly, only generate internal methods if the assembly contains a System.Runtime.CompilerServices.InternalsVisibleToAttribute.
        | ILMemberAccess.FamilyOrAssembly | ILMemberAccess.Assembly
            when cenv.hasInternalsVisibleToAttrib -> true
        | _ -> false

let canGenFieldDef (tdef: ILTypeDef) cenv (fd: ILFieldDef) =
    if not cenv.referenceAssemblyOnly then
        true
    // We want to explicitly generate fields for struct types and attributes, since they can be part of `unmanaged constraint`.
    else if tdef.IsStruct || tdef.IsKnownToBeAttribute then
        true
    else
        match fd.Access with
        | ILMemberAccess.Public -> true
        // When emitting a reference assembly, we only generate internal fields if the assembly contains a System.Runtime.CompilerServices.InternalsVisibleToAttribute.
        | ILMemberAccess.FamilyOrAssembly | ILMemberAccess.Assembly
            when cenv.hasInternalsVisibleToAttrib -> true
        | _ -> false

let canGenEventDef cenv (ev: ILEventDef) =
    if not cenv.referenceAssemblyOnly then
        true
    else
        // If we have AddMethod or RemoveMethod set (i.e. not None), try and see if we have MethodDefs for them.
        // NOTE: They can be not-None and missing MethodDefs if we skip generating them for reference assembly in the earlier pass.
        // Only generate event if we have at least add or remove, otherwise, we skip.
        [| ev.AddMethod; ev.RemoveMethod |]
        |> Array.exists (MethodDefIdxExists cenv)

let canGenPropertyDef cenv (prop: ILPropertyDef) =
    if not cenv.referenceAssemblyOnly then
        true
    else
        // If we have GetMethod or SetMethod set (i.e. not None), try and see if we have MethodDefs for them.
        // NOTE: They can be not-None and missing MethodDefs if we skip generating them for reference assembly in the earlier pass.
        // Only generate property if we have at least getter or setter, otherwise, we skip.
        [| prop.GetMethod; prop.SetMethod |]
        |> Array.choose id
        |> Array.exists (MethodDefIdxExists cenv)

let rec GetTypeDefAsRow cenv env _enc (tdef: ILTypeDef) =
    let nselem, nelem = GetTypeNameAsElemPair cenv tdef.Name
    let flags =
      if (isTypeNameForGlobalFunctions tdef.Name) then 0x00000000
      else
        int tdef.Attributes

    let tdorTag, tdorRow = GetTypeOptionAsTypeDefOrRef cenv env tdef.Extends.Value
    UnsharedRow
       [| ULong flags
          nelem
          nselem
          TypeDefOrRefOrSpec (tdorTag, tdorRow)
          SimpleIndex (TableNames.Field, cenv.fieldDefs.Count + 1)
          SimpleIndex (TableNames.Method, cenv.methodDefIdxsByKey.Count + 1) |]

and GetTypeOptionAsTypeDefOrRef cenv env tyOpt =
    match tyOpt with
    | None -> (tdor_TypeDef, 0)
    | Some ty -> (GetTypeAsTypeDefOrRef cenv env ty)

and GetTypeDefAsPropertyMapRow cenv tidx =
    UnsharedRow
        [| SimpleIndex (TableNames.TypeDef, tidx)
           SimpleIndex (TableNames.Property, cenv.propertyDefs.Count + 1) |]

and GetTypeDefAsEventMapRow cenv tidx =
    UnsharedRow
        [| SimpleIndex (TableNames.TypeDef, tidx)
           SimpleIndex (TableNames.Event, cenv.eventDefs.Count + 1) |]

and GetKeyForFieldDef tidx (fd: ILFieldDef) =
    FieldDefKey (tidx, fd.Name, fd.FieldType)

and GenFieldDefPass2 tdef cenv tidx fd =
    if canGenFieldDef tdef cenv fd then
        ignore (cenv.fieldDefs.AddUniqueEntry "field" (fun (fdkey: FieldDefKey) -> fdkey.Name) (GetKeyForFieldDef tidx fd))

and GetKeyForMethodDef cenv tidx (mdef: ILMethodDef) =
    MethodDefKey (cenv.ilg, tidx, mdef.GenericParams.Length, mdef.Name, mdef.Return.Type, mdef.ParameterTypes, mdef.CallingConv.IsStatic)

and GenMethodDefPass2 tdef cenv tidx mdef =
    if canGenMethodDef tdef cenv mdef then
        let idx =
          cenv.methodDefIdxsByKey.AddUniqueEntry
             "method"
             (fun (key: MethodDefKey) ->
               dprintn "Duplicate in method table is:"
               dprintn (" Type index: "+string key.TypeIdx)
               dprintn (" Method name: "+key.Name)
               dprintn (" Method arity (num generic params): "+string key.GenericArity)
               key.Name
             )
             (GetKeyForMethodDef cenv tidx mdef)

        cenv.methodDefIdxs[mdef] <- idx

and GetKeyForPropertyDef tidx (x: ILPropertyDef) =
    PropKey (tidx, x.Name, x.PropertyType, x.Args)

and GenPropertyDefPass2 cenv tidx x =
    if canGenPropertyDef cenv x then
        ignore (cenv.propertyDefs.AddUniqueEntry "property" (fun (PropKey (_, n, _, _)) -> n) (GetKeyForPropertyDef tidx x))

and GetTypeAsImplementsRow cenv env tidx ty =
    let tdorTag, tdorRow = GetTypeAsTypeDefOrRef cenv env ty
    UnsharedRow
        [| SimpleIndex (TableNames.TypeDef, tidx)
           TypeDefOrRefOrSpec (tdorTag, tdorRow) |]

and GenImplementsPass2 cenv env tidx ty =
    AddUnsharedRow cenv TableNames.InterfaceImpl (GetTypeAsImplementsRow cenv env tidx ty)

and GetKeyForEvent tidx (x: ILEventDef) =
    EventKey (tidx, x.Name)

and GenEventDefPass2 cenv tidx x =
    if canGenEventDef cenv x then
        ignore (cenv.eventDefs.AddUniqueEntry "event" (fun (EventKey(_, b)) -> b) (GetKeyForEvent tidx x))

and GenTypeDefPass2 pidx enc cenv (tdef: ILTypeDef) =
   try
        let env = envForTypeDef tdef
        let tidx = GetIdxForTypeDef cenv (TdKey(enc, tdef.Name))
        let tidx2 = AddUnsharedRow cenv TableNames.TypeDef (GetTypeDefAsRow cenv env enc tdef)
        if tidx <> tidx2 then failwith "index of typedef on second pass does not match index on first pass"

        // Add entries to auxiliary mapping tables, e.g. Nested, PropertyMap etc.
        // Note Nested is organised differently to the others...
        if not (isNil enc) then
            AddUnsharedRow cenv TableNames.Nested
                (UnsharedRow
                    [| SimpleIndex (TableNames.TypeDef, tidx)
                       SimpleIndex (TableNames.TypeDef, pidx) |]) |> ignore

        let props = tdef.Properties.AsList()

        if not (isNil props) then
            AddUnsharedRow cenv TableNames.PropertyMap (GetTypeDefAsPropertyMapRow cenv tidx) |> ignore

        let events = tdef.Events.AsList()
        if not (isNil events) then
            AddUnsharedRow cenv TableNames.EventMap (GetTypeDefAsEventMapRow cenv tidx) |> ignore

        // Now generate or assign index numbers for tables referenced by the maps.
        // Don't yet generate contents of these tables - leave that to pass3, as
        // code may need to embed these entries.
        cenv.implementsIdxs[tidx] <- tdef.Implements.Value |> List.map (fun x -> GenImplementsPass2 cenv env tidx x.Type)            

        tdef.Fields.AsList() |> List.iter (GenFieldDefPass2 tdef cenv tidx)
        tdef.Methods |> Seq.iter (GenMethodDefPass2 tdef cenv tidx)
        // Generation of property & event definitions for **ref assemblies** is checking existence of generated method definitions.
        // Therefore, due to mutable state within "cenv", order of operations matters.
        // Who could have thought that using shared mutable state can bring unexpected bugs...?
        props |> List.iter (GenPropertyDefPass2 cenv tidx)
        events |> List.iter (GenEventDefPass2 cenv tidx)
        tdef.NestedTypes.AsList() |> GenTypeDefsPass2 tidx (enc@[tdef.Name]) cenv
   with exn ->
     failwith ("Error in pass2 for type "+tdef.Name+", error: " + exn.Message)

and GenTypeDefsPass2 pidx enc cenv tdefs =
    List.iter (GenTypeDefPass2 pidx enc cenv) tdefs

//=====================================================================
// Pass 3 - write details of methods, fields, IL code, custom attrs etc.
//=====================================================================
let rec GetMethodDefIdx cenv mdef =
    cenv.methodDefIdxs[mdef]

and FindFieldDefIdx cenv fdkey =
    try cenv.fieldDefs.GetTableEntry fdkey
    with :? KeyNotFoundException ->
      errorR(InternalError("The local field "+fdkey.Name+" was referenced but not declared", range0))
      1

and GetFieldDefAsFieldDefIdx cenv tidx fd =
    FindFieldDefIdx cenv (GetKeyForFieldDef tidx fd)



let GetMethodRefAsMethodDefIdx cenv (mref: ILMethodRef) =
    match TryGetMethodRefAsMethodDefIdx cenv mref with
    | Result.Error msg -> failwith msg
    | Ok idx -> idx

let rec MethodRefInfoAsMemberRefRow cenv env fenv (nm, ty, callconv, args, ret, varargs, genarity) =
    MemberRefRow(GetTypeAsMemberRefParent cenv env ty,
                 GetStringHeapIdx cenv nm,
                 GetMethodRefInfoAsBlobIdx cenv fenv (callconv, args, ret, varargs, genarity))

and GetMethodRefInfoAsBlobIdx cenv env info =
    GetBytesAsBlobIdx cenv (GetCallsigAsBytes cenv env info)

let GetMethodRefInfoAsMemberRefIdx cenv env (_, ty, _, _, _, _, _ as minfo) =
    let fenv = envForMethodRef env ty
    FindOrAddSharedRow cenv TableNames.MemberRef (MethodRefInfoAsMemberRefRow cenv env fenv minfo)

let GetMethodRefInfoAsMethodRefOrDef isAlwaysMethodDef cenv env (nm, ty: ILType, cc, args, ret, varargs, genarity as minfo) =
    if Option.isNone varargs && (isAlwaysMethodDef || isTypeLocal ty) then
        if not ty.IsNominal then failwith "GetMethodRefInfoAsMethodRefOrDef: unexpected local tref-ty"
        try (mdor_MethodDef, GetMethodRefAsMethodDefIdx cenv (mkILMethRef (ty.TypeRef, cc, nm, genarity, args, ret)))
        with MethodDefNotFound -> (mdor_MemberRef, GetMethodRefInfoAsMemberRefIdx cenv env minfo)
    else (mdor_MemberRef, GetMethodRefInfoAsMemberRefIdx cenv env minfo)


// --------------------------------------------------------------------
// ILMethodSpec --> ILMethodRef/ILMethodDef/ILMethodSpec
// --------------------------------------------------------------------

let rec GetMethodSpecInfoAsMethodSpecIdx cenv env (nm, ty, cc, args, ret, varargs, minst: ILGenericArgs) =
    let mdorTag, mdorRow = GetMethodRefInfoAsMethodRefOrDef false cenv env (nm, ty, cc, args, ret, varargs, minst.Length)
    let blob =
        emitBytesViaBuffer (fun bb ->
            bb.EmitByte e_IMAGE_CEE_CS_CALLCONV_GENERICINST
            bb.EmitZ32 minst.Length
            minst |> List.iter (EmitType cenv env bb))
    FindOrAddSharedRow cenv TableNames.MethodSpec
      (SharedRow
          [| MethodDefOrRef (mdorTag, mdorRow)
             Blob (GetBytesAsBlobIdx cenv blob) |])

and GetMethodDefOrRefAsUncodedToken (tag, idx) =
    let tab =
        if tag = mdor_MethodDef then TableNames.Method
        elif tag = mdor_MemberRef then TableNames.MemberRef
        else failwith "GetMethodDefOrRefAsUncodedToken"
    getUncodedToken tab idx

and GetMethodSpecInfoAsUncodedToken cenv env (_, _, _, _, _, _, minst: ILGenericArgs as minfo) =
    if List.isEmpty minst then
        GetMethodDefOrRefAsUncodedToken (GetMethodRefInfoAsMethodRefOrDef false cenv env (GetMethodRefInfoOfMethodSpecInfo minfo))
    else
        getUncodedToken TableNames.MethodSpec (GetMethodSpecInfoAsMethodSpecIdx cenv env minfo)

and GetMethodSpecAsUncodedToken cenv env mspec =
    GetMethodSpecInfoAsUncodedToken cenv env (InfoOfMethodSpec mspec)

and GetMethodRefInfoOfMethodSpecInfo (nm, ty, cc, args, ret, varargs, minst: ILGenericArgs) =
    (nm, ty, cc, args, ret, varargs, minst.Length)

and GetMethodSpecAsMethodDefOrRef cenv env (mspec, varargs) =
    GetMethodRefInfoAsMethodRefOrDef false cenv env (GetMethodRefInfoOfMethodSpecInfo (InfoOfMethodSpec (mspec, varargs)))

and GetMethodSpecAsMethodDef cenv env (mspec, varargs) =
    GetMethodRefInfoAsMethodRefOrDef true cenv env (GetMethodRefInfoOfMethodSpecInfo (InfoOfMethodSpec (mspec, varargs)))

and InfoOfMethodSpec (mspec: ILMethodSpec, varargs) =
      (mspec.Name,
       mspec.DeclaringType,
       mspec.CallingConv,
       mspec.FormalArgTypes,
       mspec.FormalReturnType,
       varargs,
       mspec.GenericArgs)

// --------------------------------------------------------------------
// method_in_parent --> ILMethodRef/ILMethodDef
//
// Used for MethodImpls.
// --------------------------------------------------------------------

let rec GetOverridesSpecAsMemberRefIdx cenv env ospec =
    let fenv = envForOverrideSpec ospec
    let row = MethodRefInfoAsMemberRefRow cenv env fenv (ospec.MethodRef.Name, ospec.DeclaringType, ospec.MethodRef.CallingConv, ospec.MethodRef.ArgTypes, ospec.MethodRef.ReturnType, None, ospec.MethodRef.GenericArity)
    FindOrAddSharedRow cenv TableNames.MemberRef row

and GetOverridesSpecAsMethodDefOrRef cenv env (ospec: ILOverridesSpec) =
    let ty = ospec.DeclaringType
    if isTypeLocal ty then
        if not ty.IsNominal then failwith "GetOverridesSpecAsMethodDefOrRef: unexpected local tref-ty"
        try (mdor_MethodDef, GetMethodRefAsMethodDefIdx cenv ospec.MethodRef)
        with MethodDefNotFound -> (mdor_MemberRef, GetOverridesSpecAsMemberRefIdx cenv env ospec)
    else
        (mdor_MemberRef, GetOverridesSpecAsMemberRefIdx cenv env ospec)

// --------------------------------------------------------------------
// ILMethodRef --> ILMethodRef/ILMethodDef
//
// Used for Custom Attrs.
// --------------------------------------------------------------------

let rec GetMethodRefAsMemberRefIdx cenv env fenv (mref: ILMethodRef) =
    let row = MethodRefInfoAsMemberRefRow cenv env fenv (mref.Name, mkILNonGenericBoxedTy mref.DeclaringTypeRef, mref.CallingConv, mref.ArgTypes, mref.ReturnType, None, mref.GenericArity)
    FindOrAddSharedRow cenv TableNames.MemberRef row

and GetMethodRefAsCustomAttribType cenv (mref: ILMethodRef) =
    let fenv = envForNonGenericMethodRef mref
    let tref = mref.DeclaringTypeRef
    if isTypeRefLocal tref then
        try (cat_MethodDef, GetMethodRefAsMethodDefIdx cenv mref)
        with MethodDefNotFound -> (cat_MemberRef, GetMethodRefAsMemberRefIdx cenv fenv fenv mref)
    else
        (cat_MemberRef, GetMethodRefAsMemberRefIdx cenv fenv fenv mref)

// --------------------------------------------------------------------
// ILAttributes --> CustomAttribute rows
// --------------------------------------------------------------------

let rec GetCustomAttrDataAsBlobIdx cenv (data: byte[]) =
    if data.Length = 0 then 0 else GetBytesAsBlobIdx cenv data

and GetCustomAttrRow cenv hca (attr: ILAttribute) =
    let cat = GetMethodRefAsCustomAttribType cenv attr.Method.MethodRef
    let data = getCustomAttrData attr
    for element in attr.Elements do
        match element with
        | ILAttribElem.Type (Some ty) when ty.IsNominal -> GetTypeRefAsTypeRefIdx cenv ty.TypeRef |> ignore
        | ILAttribElem.TypeRef (Some tref) -> GetTypeRefAsTypeRefIdx cenv tref |> ignore
        | _ -> ()

    UnsharedRow
            [| HasCustomAttribute (fst hca, snd hca)
               CustomAttributeType (fst cat, snd cat)
               Blob (GetCustomAttrDataAsBlobIdx cenv data)
            |]

and GenCustomAttrPass3Or4 cenv hca attr =
    AddUnsharedRow cenv TableNames.CustomAttribute (GetCustomAttrRow cenv hca attr) |> ignore

and GenCustomAttrsPass3Or4 cenv hca (attrs: ILAttributes) =
    attrs.AsArray() |> Array.iter (GenCustomAttrPass3Or4 cenv hca)

// --------------------------------------------------------------------
// ILSecurityDecl --> DeclSecurity rows
// -------------------------------------------------------------------- *)

let rec GetSecurityDeclRow cenv hds (ILSecurityDecl (action, s)) =
    UnsharedRow
        [| UShort (uint16 (List.assoc action (Lazy.force ILSecurityActionMap)))
           HasDeclSecurity (fst hds, snd hds)
           Blob (GetBytesAsBlobIdx cenv s) |]

and GenSecurityDeclPass3 cenv hds attr =
    AddUnsharedRow cenv TableNames.Permission (GetSecurityDeclRow cenv hds attr) |> ignore

and GenSecurityDeclsPass3 cenv hds attrs =
    List.iter (GenSecurityDeclPass3 cenv hds) attrs

// --------------------------------------------------------------------
// ILFieldSpec --> FieldRef or ILFieldDef row
// --------------------------------------------------------------------

let rec GetFieldSpecAsMemberRefRow cenv env fenv (fspec: ILFieldSpec) =
    MemberRefRow (GetTypeAsMemberRefParent cenv env fspec.DeclaringType,
                  GetStringHeapIdx cenv fspec.Name,
                  GetFieldSpecSigAsBlobIdx cenv fenv fspec)

and GetFieldSpecAsMemberRefIdx cenv env fspec =
    let fenv = envForFieldSpec fspec
    FindOrAddSharedRow cenv TableNames.MemberRef (GetFieldSpecAsMemberRefRow cenv env fenv fspec)

// REVIEW: write into an accumulating buffer
and EmitFieldSpecSig cenv env (bb: ByteBuffer) (fspec: ILFieldSpec) =
    bb.EmitByte e_IMAGE_CEE_CS_CALLCONV_FIELD
    EmitType cenv env bb fspec.FormalType

and GetFieldSpecSigAsBytes cenv env x =
    emitBytesViaBuffer (fun bb -> EmitFieldSpecSig cenv env bb x)

and GetFieldSpecSigAsBlobIdx cenv env x =
    GetBytesAsBlobIdx cenv (GetFieldSpecSigAsBytes cenv env x)

and GetFieldSpecAsFieldDefOrRef cenv env (fspec: ILFieldSpec) =
    let ty = fspec.DeclaringType
    if isTypeLocal ty then
        if not ty.IsNominal then failwith "GetFieldSpecAsFieldDefOrRef: unexpected local tref-ty"
        let tref = ty.TypeRef
        let tidx = GetIdxForTypeDef cenv (TdKey(tref.Enclosing, tref.Name))
        let fdkey = FieldDefKey (tidx, fspec.Name, fspec.FormalType)
        (true, FindFieldDefIdx cenv fdkey)
    else
        (false, GetFieldSpecAsMemberRefIdx cenv env fspec)

and GetFieldDefOrRefAsUncodedToken (tag, idx) =
    let tab = if tag then TableNames.Field else TableNames.MemberRef
    getUncodedToken tab idx

// --------------------------------------------------------------------
// callsig --> StandAloneSig
// --------------------------------------------------------------------

let GetCallsigAsBlobIdx cenv env (callsig: ILCallingSignature, varargs) =
    GetBytesAsBlobIdx cenv
      (GetCallsigAsBytes cenv env (callsig.CallingConv,
                                      callsig.ArgTypes,
                                      callsig.ReturnType, varargs, 0))

let GetCallsigAsStandAloneSigRow cenv env x =
    SharedRow [| Blob (GetCallsigAsBlobIdx cenv env x) |]

let GetCallsigAsStandAloneSigIdx cenv env info =
    FindOrAddSharedRow cenv TableNames.StandAloneSig (GetCallsigAsStandAloneSigRow cenv env info)

// --------------------------------------------------------------------
// local signatures --> BlobHeap idx
// --------------------------------------------------------------------

let EmitLocalSig cenv env (bb: ByteBuffer) (locals: ILLocals) =
    bb.EmitByte e_IMAGE_CEE_CS_CALLCONV_LOCAL_SIG
    bb.EmitZ32 locals.Length
    locals |> List.iter (EmitLocalInfo cenv env bb)

let GetLocalSigAsBlobHeapIdx cenv env locals =
    GetBytesAsBlobIdx cenv (emitBytesViaBuffer (fun bb -> EmitLocalSig cenv env bb locals))

let GetLocalSigAsStandAloneSigIdx cenv env locals =
    SharedRow [| Blob (GetLocalSigAsBlobHeapIdx cenv env locals) |]



type ExceptionClauseKind =
  | FinallyClause
  | FaultClause
  | TypeFilterClause of int32
  | FilterClause of int

type ExceptionClauseSpec = int * int * int * int * ExceptionClauseKind

/// Arbitrary value
[<Literal>]
let CodeBufferCapacity = 200 

/// Buffer to write results of emitting code into. Also record:
///   - branch sources (where fixups will occur)
///   - possible branch destinations
///   - locations of embedded handles into the string table
///   - the exception table
type CodeBuffer =
    {
      code: ByteBuffer

      /// (instruction; optional short form); start of instr in code buffer; code loc for the end of the instruction the fixup resides in ; where is the destination of the fixup
      mutable reqdBrFixups: ((int * int option) * int * ILCodeLabel list) list

      availBrFixups: Dictionary<ILCodeLabel, int>

      /// code loc to fixup in code buffer
      mutable reqdStringFixupsInMethod: (int * int) list

      /// data for exception handling clauses
      mutable seh: ExceptionClauseSpec list

      seqpoints: ResizeArray<PdbDebugPoint>
    }

    interface IDisposable with
        member this.Dispose() =
            (this.code :> IDisposable).Dispose()

    static member Create _nm =
        { seh = []
          code= ByteBuffer.Create CodeBufferCapacity
          reqdBrFixups=[]
          reqdStringFixupsInMethod=[]
          availBrFixups = Dictionary<_, _>(10, HashIdentity.Structural)
          seqpoints = ResizeArray<_>(10)
        }

    member codebuf.EmitExceptionClause seh = codebuf.seh <- seh :: codebuf.seh

    member codebuf.EmitSeqPoint cenv (m: ILDebugPoint) =
        if cenv.generatePdb then
          // table indexes are 1-based, document array indexes are 0-based
          let doc = (cenv.documents.FindOrAddSharedEntry m.Document) - 1
          codebuf.seqpoints.Add
            { Document=doc
              Offset= codebuf.code.Position
              Line=m.Line
              Column=m.Column
              EndLine=m.EndLine
              EndColumn=m.EndColumn }

    member codebuf.EmitByte x = codebuf.code.EmitIntAsByte x

    member codebuf.EmitUInt16 x = codebuf.code.EmitUInt16 x

    member codebuf.EmitInt32 x = codebuf.code.EmitInt32 x

    member codebuf.EmitInt64 x = codebuf.code.EmitInt64 x

    member codebuf.EmitUncodedToken u = codebuf.EmitInt32 u

    member codebuf.RecordReqdStringFixup stringIdx =
        codebuf.reqdStringFixupsInMethod <- (codebuf.code.Position, stringIdx) :: codebuf.reqdStringFixupsInMethod
        // Write a special value in that we check later when applying the fixup
        codebuf.EmitInt32 0xdeadbeef

    member codebuf.RecordReqdBrFixups i tgs =
        codebuf.reqdBrFixups <- (i, codebuf.code.Position, tgs) :: codebuf.reqdBrFixups
        // Write a special value in that we check later when applying the fixup
        // Value is 0x11 {deadbbbb}* where 11 is for the instruction and deadbbbb is for each target
        codebuf.EmitByte 0x11 // for the instruction
        (if fst i = i_switch then
          codebuf.EmitInt32 tgs.Length)
        List.iter (fun _ -> codebuf.EmitInt32 0xdeadbbbb) tgs

    member codebuf.RecordReqdBrFixup i tg = codebuf.RecordReqdBrFixups i [tg]

    member codebuf.RecordAvailBrFixup tg =
        codebuf.availBrFixups[tg] <- codebuf.code.Position

/// Applying branch fixups. Use short versions of instructions
/// wherever possible. Sadly we can only determine if we can use a short
/// version after we've layed out the code for all other instructions.
/// This in turn means that using a short version may change
/// the various offsets into the code.
module Codebuf =

    let binaryChop p (arr: 'T[]) =
        let rec go n m =
            if n > m then raise (KeyNotFoundException("binary chop did not find element"))
            else
                let i = (n+m)/2
                let c = p arr[i]
                if c = 0 then i elif c < 0 then go n (i-1) else go (i+1) m
        go 0 (Array.length arr)

    let applyBrFixups (origCode : byte[]) origExnClauses origReqdStringFixups (origAvailBrFixups: Dictionary<ILCodeLabel, int>) origReqdBrFixups origSeqPoints origScopes =
      let orderedOrigReqdBrFixups = origReqdBrFixups |> List.sortBy (fun (_, fixupLoc, _) -> fixupLoc)

      use newCode = ByteBuffer.Create origCode.Length

      // Copy over all the code, working out whether the branches will be short
      // or long and adjusting the branch destinations. Record an adjust function to adjust all the other
      // gumpf that refers to fixed offsets in the code stream.
      let newCode, newReqdBrFixups, adjuster =
          let mutable remainingReqdFixups = orderedOrigReqdBrFixups
          let mutable origWhere = 0
          let mutable newWhere = 0
          let mutable doneLast = false
          let mutable newReqdBrFixups = []
          let mutable adjustments = []

          while (remainingReqdFixups <> [] || not doneLast) do
              let doingLast = isNil remainingReqdFixups
              let origStartOfNoBranchBlock = origWhere
              let newStartOfNoBranchBlock = newWhere

              let origEndOfNoBranchBlock =
                if doingLast then origCode.Length
                else
                  let _, origStartOfInstr, _ = List.head remainingReqdFixups
                  origStartOfInstr

              // Copy over a chunk of non-branching code
              let nobranch_len = origEndOfNoBranchBlock - origStartOfNoBranchBlock
              newCode.EmitBytes origCode[origStartOfNoBranchBlock..origStartOfNoBranchBlock+nobranch_len-1]

              // Record how to adjust addresses in this range, including the branch instruction
              // we write below, or the end of the method if we're doing the last bblock
              adjustments <- (origStartOfNoBranchBlock, origEndOfNoBranchBlock, newStartOfNoBranchBlock) :: adjustments

              // Increment locations to the branch instruction we're really interested in
              origWhere <- origEndOfNoBranchBlock
              newWhere <- newWhere + nobranch_len

              // Now do the branch instruction. Decide whether the fixup will be short or long in the new code
              if doingLast then
                  doneLast <- true
              else
                  let (i, origStartOfInstr, tgs: ILCodeLabel list) = List.head remainingReqdFixups
                  remainingReqdFixups <-List.tail remainingReqdFixups
                  if origCode[origStartOfInstr] <> 0x11uy then failwith "br fixup sanity check failed (1)"
                  let i_length = if fst i = i_switch then 5 else 1
                  origWhere <- origWhere + i_length

                  let origEndOfInstr = origStartOfInstr + i_length + 4 * tgs.Length
                  let newEndOfInstrIfSmall = newWhere + i_length + 1
                  let newEndOfInstrIfBig = newWhere + i_length + 4 * tgs.Length

                  let short =
                    match i, tgs with
                    | (_, Some i_short), [tg]
                        when
                           // Use the original offsets to compute if the branch is small or large. This is
                           // a safe approximation because code only gets smaller.
                           (let origDest =
                                match origAvailBrFixups.TryGetValue tg with
                                | true, fixup -> fixup
                                | _ ->
                                    dprintn ("branch target " + formatCodeLabel tg + " not found in code")
                                    666666
                            let origRelOffset = origDest - origEndOfInstr
                            -128 <= origRelOffset && origRelOffset <= 127)
                      ->
                        newCode.EmitIntAsByte i_short
                        true
                    | (i_long, _), _ ->
                        newCode.EmitIntAsByte i_long
                        (if i_long = i_switch then
                          newCode.EmitInt32 tgs.Length)
                        false

                  newWhere <- newWhere + i_length
                  if newWhere <> newCode.Position then dprintn "mismatch between newWhere and newCode"

                  tgs |> List.iter (fun tg ->
                        let origFixupLoc = origWhere
                        checkFixup32 origCode origFixupLoc 0xdeadbbbb

                        if short then
                            newReqdBrFixups <- (newWhere, newEndOfInstrIfSmall, tg, true) :: newReqdBrFixups
                            newCode.EmitIntAsByte 0x98 (* sanity check *)
                            newWhere <- newWhere + 1
                        else
                            newReqdBrFixups <- (newWhere, newEndOfInstrIfBig, tg, false) :: newReqdBrFixups
                            newCode.EmitInt32 0xf00dd00f (* sanity check *)
                            newWhere <- newWhere + 4
                        if newWhere <> newCode.Position then dprintn "mismatch between newWhere and newCode"
                        origWhere <- origWhere + 4)

                  if origWhere <> origEndOfInstr then dprintn "mismatch between origWhere and origEndOfInstr"

          let adjuster =
            let arr = Array.ofList (List.rev adjustments)
            fun addr ->
              let i =
                  try binaryChop (fun (a1, a2, _) -> if addr < a1 then -1 elif addr > a2 then 1 else 0) arr
                  with
                     :? KeyNotFoundException ->
                         failwith ("adjuster: address "+string addr+" is out of range")
              let origStartOfNoBranchBlock, _, newStartOfNoBranchBlock = arr[i]
              addr - (origStartOfNoBranchBlock - newStartOfNoBranchBlock)

          newCode.AsMemory().ToArray(),
          newReqdBrFixups,
          adjuster

      // Now adjust everything
      let newAvailBrFixups =
          let tab = Dictionary<_, _>(10, HashIdentity.Structural)
          for KeyValue(tglab, origBrDest) in origAvailBrFixups do
              tab[tglab] <- adjuster origBrDest
          tab
      let newReqdStringFixups = List.map (fun (origFixupLoc, stok) -> adjuster origFixupLoc, stok) origReqdStringFixups
      let newSeqPoints = Array.map (fun (sp: PdbDebugPoint) -> {sp with Offset=adjuster sp.Offset}) origSeqPoints
      let newExnClauses =
          origExnClauses |> List.map (fun (st1, sz1, st2, sz2, kind) ->
              (adjuster st1, (adjuster (st1 + sz1) - adjuster st1),
               adjuster st2, (adjuster (st2 + sz2) - adjuster st2),
               (match kind with
               | FinallyClause | FaultClause | TypeFilterClause _ -> kind
               | FilterClause n -> FilterClause (adjuster n))))

      let newScopes =
        let rec remap scope =
          {scope with StartOffset = adjuster scope.StartOffset
                      EndOffset = adjuster scope.EndOffset
                      Children = Array.map remap scope.Children }
        List.map remap origScopes

      // Now apply the adjusted fixups in the new code
      newReqdBrFixups |> List.iter (fun (newFixupLoc, endOfInstr, tg, small) ->
          match newAvailBrFixups.TryGetValue tg with
          | true, n ->
              let relOffset = n - endOfInstr
              if small then
                  if Bytes.get newCode newFixupLoc <> 0x98 then failwith "br fixup sanity check failed"
                  newCode[newFixupLoc] <- b0 relOffset
              else
                  checkFixup32 newCode newFixupLoc 0xf00dd00fl
                  applyFixup32 newCode newFixupLoc relOffset
          | _ -> failwith ("target " + formatCodeLabel tg + " not found in new fixups"))

      newCode, newReqdStringFixups, newExnClauses, newSeqPoints, newScopes

    // --------------------------------------------------------------------
    // Structured residue of emitting instructions: SEH exception handling
    // and scopes for local variables.
    // --------------------------------------------------------------------

    // Emitting instructions generates a tree of seh specifications
    // We then emit the exception handling specs separately.
    // nb. ECMA spec says the SEH blocks must be returned inside-out
    type SEHTree =
      | Node of ExceptionClauseSpec option * SEHTree list

    // --------------------------------------------------------------------
    // Table of encodings for instructions without arguments, also indexes
    // for all instructions.
    // --------------------------------------------------------------------

    let encodingsForNoArgInstrs = Dictionary<_, _>(300, HashIdentity.Structural)
    let _ =
      List.iter
        (fun (x, mk) -> encodingsForNoArgInstrs[mk] <- x)
        (noArgInstrs.Force())
    let encodingsOfNoArgInstr si = encodingsForNoArgInstrs[si]

    // --------------------------------------------------------------------
    // Emit instructions
    // --------------------------------------------------------------------

    /// Emit the code for an instruction
    let emitInstrCode (codebuf: CodeBuffer) i =
        if i > 0xFF then
            assert (i >>> 8 = 0xFE)
            codebuf.EmitByte ((i >>> 8) &&& 0xFF)
            codebuf.EmitByte (i &&& 0xFF)
        else
            codebuf.EmitByte i

    let emitTypeInstr cenv codebuf env i ty =
        emitInstrCode codebuf i
        codebuf.EmitUncodedToken (getTypeDefOrRefAsUncodedToken (GetTypeAsTypeDefOrRef cenv env ty))

    let emitMethodSpecInfoInstr cenv codebuf env i mspecinfo =
        emitInstrCode codebuf i
        codebuf.EmitUncodedToken (GetMethodSpecInfoAsUncodedToken cenv env mspecinfo)

    let emitMethodSpecInstr cenv codebuf env i mspec =
        emitInstrCode codebuf i
        codebuf.EmitUncodedToken (GetMethodSpecAsUncodedToken cenv env mspec)

    let emitFieldSpecInstr cenv codebuf env i fspec =
        emitInstrCode codebuf i
        codebuf.EmitUncodedToken (GetFieldDefOrRefAsUncodedToken (GetFieldSpecAsFieldDefOrRef cenv env fspec))

    let emitShortUInt16Instr codebuf (i_short, i) x =
        let n = int32 x
        if n <= 255 then
            emitInstrCode codebuf i_short
            codebuf.EmitByte n
        else
            emitInstrCode codebuf i
            codebuf.EmitUInt16 x

    let emitShortInt32Instr codebuf (i_short, i) x =
        if x >= -128 && x <= 127 then
            emitInstrCode codebuf i_short
            codebuf.EmitByte (if x < 0x0 then x + 256 else x)
        else
            emitInstrCode codebuf i
            codebuf.EmitInt32 x

    let emitTailness (cenv: cenv) codebuf tl =
        if tl = Tailcall && cenv.emitTailcalls then emitInstrCode codebuf i_tail

    let emitVolatility codebuf tl =
        if tl = Volatile then emitInstrCode codebuf i_volatile

    let emitConstrained cenv codebuf env ty =
        emitInstrCode codebuf i_constrained
        codebuf.EmitUncodedToken (getTypeDefOrRefAsUncodedToken (GetTypeAsTypeDefOrRef cenv env ty))

    let emitAlignment codebuf tl =
        match tl with
        | Aligned -> ()
        | Unaligned1 -> emitInstrCode codebuf i_unaligned; codebuf.EmitByte 0x1
        | Unaligned2 -> emitInstrCode codebuf i_unaligned; codebuf.EmitByte 0x2
        | Unaligned4 -> emitInstrCode codebuf i_unaligned; codebuf.EmitByte 0x4

    let rec emitInstr cenv codebuf env instr =
        match instr with
        | si when isNoArgInstr si ->
             emitInstrCode codebuf (encodingsOfNoArgInstr si)
        | I_brcmp (cmp, tg1) ->
            codebuf.RecordReqdBrFixup ((Lazy.force ILCmpInstrMap)[cmp], Some (Lazy.force ILCmpInstrRevMap).[cmp]) tg1
        | I_br tg -> codebuf.RecordReqdBrFixup (i_br, Some i_br_s) tg
        | I_seqpoint s -> codebuf.EmitSeqPoint cenv s
        | I_leave tg -> codebuf.RecordReqdBrFixup (i_leave, Some i_leave_s) tg
        | I_call (tl, mspec, varargs) ->
            emitTailness cenv codebuf tl
            emitMethodSpecInstr cenv codebuf env i_call (mspec, varargs)
            //emitAfterTailcall codebuf tl
        | I_callvirt (tl, mspec, varargs) ->
            emitTailness cenv codebuf tl
            emitMethodSpecInstr cenv codebuf env i_callvirt (mspec, varargs)
            //emitAfterTailcall codebuf tl
        | I_callconstraint (callvirt, tl, ty, mspec, varargs) ->
            emitTailness cenv codebuf tl
            emitConstrained cenv codebuf env ty
            let instr = if callvirt then i_callvirt else i_call
            emitMethodSpecInstr cenv codebuf env instr (mspec, varargs)
            //emitAfterTailcall codebuf tl
        | I_newobj (mspec, varargs) ->
            emitMethodSpecInstr cenv codebuf env i_newobj (mspec, varargs)
        | I_ldftn mspec ->
            emitMethodSpecInstr cenv codebuf env i_ldftn (mspec, None)
        | I_ldvirtftn mspec ->
            emitMethodSpecInstr cenv codebuf env i_ldvirtftn (mspec, None)

        | I_calli (tl, callsig, varargs) ->
            emitTailness cenv codebuf tl
            emitInstrCode codebuf i_calli
            codebuf.EmitUncodedToken (getUncodedToken TableNames.StandAloneSig (GetCallsigAsStandAloneSigIdx cenv env (callsig, varargs)))
            //emitAfterTailcall codebuf tl

        | I_ldarg u16 -> emitShortUInt16Instr codebuf (i_ldarg_s, i_ldarg) u16
        | I_starg u16 -> emitShortUInt16Instr codebuf (i_starg_s, i_starg) u16
        | I_ldarga u16 -> emitShortUInt16Instr codebuf (i_ldarga_s, i_ldarga) u16
        | I_ldloc u16 -> emitShortUInt16Instr codebuf (i_ldloc_s, i_ldloc) u16
        | I_stloc u16 -> emitShortUInt16Instr codebuf (i_stloc_s, i_stloc) u16
        | I_ldloca u16 -> emitShortUInt16Instr codebuf (i_ldloca_s, i_ldloca) u16

        | I_cpblk (al, vol) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitInstrCode codebuf i_cpblk
        | I_initblk (al, vol) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitInstrCode codebuf i_initblk

        | AI_ldc (DT_I4, ILConst.I4 x) ->
            emitShortInt32Instr codebuf (i_ldc_i4_s, i_ldc_i4) x
        | AI_ldc (DT_I8, ILConst.I8 x) ->
            emitInstrCode codebuf i_ldc_i8
            codebuf.EmitInt64 x
        | AI_ldc (_, ILConst.R4 x) ->
            emitInstrCode codebuf i_ldc_r4
            codebuf.EmitInt32 (bitsOfSingle x)
        | AI_ldc (_, ILConst.R8 x) ->
            emitInstrCode codebuf i_ldc_r8
            codebuf.EmitInt64 (bitsOfDouble x)

        | I_ldind (al, vol, dt) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitInstrCode codebuf
              (match dt with
              | DT_I -> i_ldind_i
              | DT_I1 -> i_ldind_i1
              | DT_I2 -> i_ldind_i2
              | DT_I4 -> i_ldind_i4
              | DT_U1 -> i_ldind_u1
              | DT_U2 -> i_ldind_u2
              | DT_U4 -> i_ldind_u4
              | DT_I8 -> i_ldind_i8
              | DT_R4 -> i_ldind_r4
              | DT_R8 -> i_ldind_r8
              | DT_REF -> i_ldind_ref
              | _ -> failwith "ldind")

        | I_stelem dt ->
            emitInstrCode codebuf
              (match dt with
              | DT_I | DT_U -> i_stelem_i
              | DT_U1 | DT_I1 -> i_stelem_i1
              | DT_I2 | DT_U2 -> i_stelem_i2
              | DT_I4 | DT_U4 -> i_stelem_i4
              | DT_I8 | DT_U8 -> i_stelem_i8
              | DT_R4 -> i_stelem_r4
              | DT_R8 -> i_stelem_r8
              | DT_REF -> i_stelem_ref
              | _ -> failwith "stelem")

        | I_ldelem dt ->
            emitInstrCode codebuf
              (match dt with
              | DT_I | DT_U -> i_ldelem_i
              | DT_I1 -> i_ldelem_i1
              | DT_I2 -> i_ldelem_i2
              | DT_I4 -> i_ldelem_i4
              | DT_I8 | DT_U8 -> i_ldelem_i8
              | DT_U1 -> i_ldelem_u1
              | DT_U2 -> i_ldelem_u2
              | DT_U4 -> i_ldelem_u4
              | DT_R4 -> i_ldelem_r4
              | DT_R8 -> i_ldelem_r8
              | DT_REF -> i_ldelem_ref
              | _ -> failwith "ldelem")

        | I_stind (al, vol, dt) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitInstrCode codebuf
              (match dt with
              | DT_U | DT_I -> i_stind_i
              | DT_U1 | DT_I1 -> i_stind_i1
              | DT_U2 | DT_I2 -> i_stind_i2
              | DT_U4 | DT_I4 -> i_stind_i4
              | DT_U8 | DT_I8 -> i_stind_i8
              | DT_R4 -> i_stind_r4
              | DT_R8 -> i_stind_r8
              | DT_REF -> i_stind_ref
              | _ -> failwith "stelem")

        | I_switch labs -> codebuf.RecordReqdBrFixups (i_switch, None) labs

        | I_ldfld (al, vol, fspec) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitFieldSpecInstr cenv codebuf env i_ldfld fspec
        | I_ldflda fspec ->
            emitFieldSpecInstr cenv codebuf env i_ldflda fspec
        | I_ldsfld (vol, fspec) ->
            emitVolatility codebuf vol
            emitFieldSpecInstr cenv codebuf env i_ldsfld fspec
        | I_ldsflda fspec ->
            emitFieldSpecInstr cenv codebuf env i_ldsflda fspec
        | I_stfld (al, vol, fspec) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitFieldSpecInstr cenv codebuf env i_stfld fspec
        | I_stsfld (vol, fspec) ->
            emitVolatility codebuf vol
            emitFieldSpecInstr cenv codebuf env i_stsfld fspec

        | I_ldtoken tok ->
            emitInstrCode codebuf i_ldtoken
            codebuf.EmitUncodedToken
              (match tok with
              | ILToken.ILType ty ->
                  match GetTypeAsTypeDefOrRef cenv env ty with
                  | tag, idx when tag = tdor_TypeDef -> getUncodedToken TableNames.TypeDef idx
                  | tag, idx when tag = tdor_TypeRef -> getUncodedToken TableNames.TypeRef idx
                  | tag, idx when tag = tdor_TypeSpec -> getUncodedToken TableNames.TypeSpec idx
                  | _ -> failwith "?"
              | ILToken.ILMethod mspec ->
                  match GetMethodSpecAsMethodDefOrRef cenv env (mspec, None) with
                  | tag, idx when tag = mdor_MethodDef -> getUncodedToken TableNames.Method idx
                  | tag, idx when tag = mdor_MemberRef -> getUncodedToken TableNames.MemberRef idx
                  | _ -> failwith "?"

              | ILToken.ILField fspec ->
                  match GetFieldSpecAsFieldDefOrRef cenv env fspec with
                  | true, idx -> getUncodedToken TableNames.Field idx
                  | false, idx -> getUncodedToken TableNames.MemberRef idx)
        | I_ldstr s ->
            emitInstrCode codebuf i_ldstr
            codebuf.RecordReqdStringFixup (GetUserStringHeapIdx cenv s)

        | I_box ty -> emitTypeInstr cenv codebuf env i_box ty
        | I_unbox ty -> emitTypeInstr cenv codebuf env i_unbox ty
        | I_unbox_any ty -> emitTypeInstr cenv codebuf env i_unbox_any ty

        | I_newarr (shape, ty) ->
            if (shape = ILArrayShape.SingleDimensional) then
                emitTypeInstr cenv codebuf env i_newarr ty
            else
                let args = List.init shape.Rank (fun _ -> cenv.ilg.typ_Int32)
                emitMethodSpecInfoInstr cenv codebuf env i_newobj (".ctor", mkILArrTy(ty, shape), ILCallingConv.Instance, args, ILType.Void, None, [])

        | I_stelem_any (shape, ty) ->
            if (shape = ILArrayShape.SingleDimensional) then
                emitTypeInstr cenv codebuf env i_stelem_any ty
            else
                let args = List.init (shape.Rank+1) (fun i -> if i < shape.Rank then cenv.ilg.typ_Int32 else ty)
                emitMethodSpecInfoInstr cenv codebuf env i_call ("Set", mkILArrTy(ty, shape), ILCallingConv.Instance, args, ILType.Void, None, [])

        | I_ldelem_any (shape, ty) ->
            if (shape = ILArrayShape.SingleDimensional) then
                emitTypeInstr cenv codebuf env i_ldelem_any ty
            else
                let args = List.init shape.Rank (fun _ -> cenv.ilg.typ_Int32)
                emitMethodSpecInfoInstr cenv codebuf env i_call ("Get", mkILArrTy(ty, shape), ILCallingConv.Instance, args, ty, None, [])

        | I_ldelema (ro, _isNativePtr, shape, ty) ->
            if (ro = ReadonlyAddress) then
                emitInstrCode codebuf i_readonly
            if (shape = ILArrayShape.SingleDimensional) then
                emitTypeInstr cenv codebuf env i_ldelema ty
            else
                let args = List.init shape.Rank (fun _ -> cenv.ilg.typ_Int32)
                emitMethodSpecInfoInstr cenv codebuf env i_call ("Address", mkILArrTy(ty, shape), ILCallingConv.Instance, args, ILType.Byref ty, None, [])

        | I_castclass ty -> emitTypeInstr cenv codebuf env i_castclass ty
        | I_isinst ty -> emitTypeInstr cenv codebuf env i_isinst ty
        | I_refanyval ty -> emitTypeInstr cenv codebuf env i_refanyval ty
        | I_mkrefany ty -> emitTypeInstr cenv codebuf env i_mkrefany ty
        | I_initobj ty -> emitTypeInstr cenv codebuf env i_initobj ty
        | I_ldobj (al, vol, ty) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitTypeInstr cenv codebuf env i_ldobj ty
        | I_stobj (al, vol, ty) ->
            emitAlignment codebuf al
            emitVolatility codebuf vol
            emitTypeInstr cenv codebuf env i_stobj ty
        | I_cpobj ty -> emitTypeInstr cenv codebuf env i_cpobj ty
        | I_sizeof ty -> emitTypeInstr cenv codebuf env i_sizeof ty
        | EI_ldlen_multi (_, m) ->
            emitShortInt32Instr codebuf (i_ldc_i4_s, i_ldc_i4) m
            emitInstr cenv codebuf env (mkNormalCall(mkILNonGenericMethSpecInTy(cenv.ilg.typ_Array, ILCallingConv.Instance, "GetLength", [cenv.ilg.typ_Int32], cenv.ilg.typ_Int32)))

        | _ -> failwith "an IL instruction cannot be emitted"


    let mkScopeNode cenv importScope (localSigs: _[]) (startOffset, endOffset, ls: ILLocalDebugMapping list, childScopes) =
        if isNil ls || not cenv.generatePdb then
            childScopes
        else
          [ { Children= Array.ofList childScopes
              StartOffset=startOffset
              EndOffset=endOffset
              Locals=
                  [| for x in ls do 
                       if x.LocalName <> "" then
                           { Name=x.LocalName
                             Signature= (try localSigs[x.LocalIndex] with _ -> failwith ("local variable index "+string x.LocalIndex+"in debug info does not reference a valid local"))
                             Index= x.LocalIndex } |]
              Imports = importScope
            } ]


    // Used to put local debug scopes and exception handlers into a tree form
    let rangeInsideRange (start_pc1, end_pc1) (start_pc2, end_pc2) =
      (start_pc1: int) >= start_pc2 && start_pc1 < end_pc2 &&
      (end_pc1: int) > start_pc2 && end_pc1 <= end_pc2

    let lranges_of_clause cl =
      match cl with
      | ILExceptionClause.Finally r1 -> [r1]
      | ILExceptionClause.Fault r1 -> [r1]
      | ILExceptionClause.FilterCatch (r1, r2) -> [r1;r2]
      | ILExceptionClause.TypeCatch (_ty, r1) -> [r1]


    let labelsToRange (lab2pc : Dictionary<ILCodeLabel, int>) p = let l1, l2 = p in lab2pc[l1], lab2pc[l2]

    let labelRangeInsideLabelRange lab2pc ls1 ls2 =
        rangeInsideRange (labelsToRange lab2pc ls1) (labelsToRange lab2pc ls2)

    let findRoots contains vs =
        // For each item, either make it a root or make it a child of an existing root
        let addToRoot roots x =
            // Look to see if 'x' is inside one of the roots
            let roots, found =
                (false, roots) ||> List.mapFold (fun found (r, children) ->
                    if found then ((r, children), true)
                    elif contains x r then ((r, x :: children), true)
                    else ((r, children), false))

            if found then roots
            else
                // Find the ones that 'x' encompasses and collapse them
                let yes, others = roots |> List.partition (fun (r, _) -> contains r x)
                (x, yes |> List.collect (fun (r, ch) -> r :: ch)) :: others

        ([], vs) ||> List.fold addToRoot

    let rec makeSEHTree cenv env (pc2pos: int[]) (lab2pc : Dictionary<ILCodeLabel, int>) (exs : ILExceptionSpec list) =

        let clause_inside_lrange cl lr =
          List.forall (fun lr1 -> labelRangeInsideLabelRange lab2pc lr1 lr) (lranges_of_clause cl)

        let tryspec_inside_lrange (tryspec1: ILExceptionSpec) lr =
          (labelRangeInsideLabelRange lab2pc tryspec1.Range lr && clause_inside_lrange tryspec1.Clause lr)

        let tryspec_inside_clause tryspec1 cl =
          List.exists (fun lr -> tryspec_inside_lrange tryspec1 lr) (lranges_of_clause cl)

        let tryspec_inside_tryspec tryspec1 (tryspec2: ILExceptionSpec) =
          tryspec_inside_lrange tryspec1 tryspec2.Range ||
          tryspec_inside_clause tryspec1 tryspec2.Clause

        let roots = findRoots tryspec_inside_tryspec exs
        let trees =
            roots |> List.map (fun (cl, ch) ->
                let r1 = labelsToRange lab2pc cl.Range
                let conv ((s1, e1), (s2, e2)) x = pc2pos[s1], pc2pos[e1] - pc2pos[s1], pc2pos[s2], pc2pos[e2] - pc2pos[s2], x
                let children = makeSEHTree cenv env pc2pos lab2pc ch
                let n =
                    match cl.Clause with
                    | ILExceptionClause.Finally r2 ->
                        conv (r1, labelsToRange lab2pc r2) ExceptionClauseKind.FinallyClause
                    | ILExceptionClause.Fault r2 ->
                        conv (r1, labelsToRange lab2pc r2) ExceptionClauseKind.FaultClause
                    | ILExceptionClause.FilterCatch ((filterStart, _), r3) ->
                        conv (r1, labelsToRange lab2pc r3) (ExceptionClauseKind.FilterClause pc2pos[lab2pc[filterStart]])
                    | ILExceptionClause.TypeCatch (ty, r2) ->
                        conv (r1, labelsToRange lab2pc r2) (TypeFilterClause (getTypeDefOrRefAsUncodedToken (GetTypeAsTypeDefOrRef cenv env ty)))
                SEHTree.Node (Some n, children) )

        trees

    let rec makeLocalsTree cenv importScope localSigs (pc2pos: int[]) (lab2pc : Dictionary<ILCodeLabel, int>) (exs : ILLocalDebugInfo list) =
        let localInsideLocal (locspec1: ILLocalDebugInfo) (locspec2: ILLocalDebugInfo) =
          labelRangeInsideLabelRange lab2pc locspec1.Range locspec2.Range

        let roots = findRoots localInsideLocal exs

        let trees =
            roots |> List.collect (fun (cl, ch) ->
                let s1, e1 = labelsToRange lab2pc cl.Range
                let s1, e1 = pc2pos[s1], pc2pos[e1]
                let children = makeLocalsTree cenv importScope localSigs pc2pos lab2pc ch
                mkScopeNode cenv importScope localSigs (s1, e1, cl.DebugMappings, children))
        trees

    // Emit the SEH tree
    let rec emitExceptionHandlerTree (codebuf: CodeBuffer) (Node (x, childSEH)) =
        List.iter (emitExceptionHandlerTree codebuf) childSEH // internal first
        x |> Option.iter codebuf.EmitExceptionClause

    let emitCode cenv importScope localSigs (codebuf: CodeBuffer) env (code: ILCode) =
        let instrs = code.Instrs

        // Build a table mapping Abstract IL pcs to positions in the generated code buffer
        let pc2pos = Array.zeroCreate (instrs.Length+1)
        let pc2labs = Dictionary()
        for KeyValue (lab, pc) in code.Labels do
            match pc2labs.TryGetValue pc with
            | true, labels ->
                pc2labs[pc] <- lab :: labels
            | _ -> pc2labs[pc] <- [lab]

        // Emit the instructions
        for pc = 0 to instrs.Length do
            match pc2labs.TryGetValue pc with
            | true, labels ->
                for lab in labels do
                    codebuf.RecordAvailBrFixup lab
            | _ -> ()
            pc2pos[pc] <- codebuf.code.Position
            if pc < instrs.Length then
                match instrs[pc] with
                | I_br l when code.Labels[l] = pc + 1 -> () // compress I_br to next instruction
                | i -> emitInstr cenv codebuf env i

        // Build the exceptions and locals information, ready to emit
        let SEHTree = makeSEHTree cenv env pc2pos code.Labels code.Exceptions
        List.iter (emitExceptionHandlerTree codebuf) SEHTree

        // Build the locals information, ready to emit
        let localsTree = makeLocalsTree cenv importScope localSigs pc2pos code.Labels code.Locals

        // Adjust the scopes for shadowing
        let unshadowed = List.collect (unshadowScopes >> Array.toList) localsTree
        unshadowed

    let EmitMethodCode cenv importScope localSigs env nm code =
        use codebuf = CodeBuffer.Create nm
        let origScopes = emitCode cenv importScope localSigs codebuf env code
        let origCode = codebuf.code.AsMemory().ToArray()
        let origExnClauses = List.rev codebuf.seh
        let origReqdStringFixups = codebuf.reqdStringFixupsInMethod
        let origAvailBrFixups = codebuf.availBrFixups
        let origReqdBrFixups = codebuf.reqdBrFixups
        let origSeqPoints = codebuf.seqpoints.ToArray()

        let newCode, newReqdStringFixups, newExnClauses, newSeqPoints, newScopes =
            applyBrFixups origCode origExnClauses origReqdStringFixups origAvailBrFixups origReqdBrFixups origSeqPoints origScopes

        let rootScope =
            { 
              Children= Array.ofList newScopes
              StartOffset=0
              EndOffset=newCode.Length
              Locals=[| |] 
              Imports = importScope 
            }

        (newReqdStringFixups, newExnClauses, newCode, newSeqPoints, rootScope)

// --------------------------------------------------------------------
// ILMethodBody --> bytes
// --------------------------------------------------------------------
let GetFieldDefTypeAsBlobIdx cenv env ty =
    let bytes = emitBytesViaBuffer (fun bb -> bb.EmitByte e_IMAGE_CEE_CS_CALLCONV_FIELD
                                              EmitType cenv env bb ty)
    GetBytesAsBlobIdx cenv bytes

let GenPdbImport (cenv: cenv) (input: ILDebugImport) =
    match input with 
    | ILDebugImport.ImportType ty ->
        let tspec = ty.TypeSpec
        let tok = getTypeInfoAsTypeDefOrRefEncoded cenv (tspec.Scope, tspec.Enclosing, tspec.Name)
        PdbImport.ImportType tok

    | ILDebugImport.ImportNamespace nsp -> PdbImport.ImportNamespace nsp

let rec GenPdbImports (cenv: cenv) (input: ILDebugImports option) =
    match input with 
    | None -> None
    | Some ilImports -> 
        match cenv.pdbImports.TryGetValue(ilImports) with
        | true, v -> Some v
        | _ ->
            let v : PdbImports = 
                { Imports = ilImports.Imports |> Array.map (GenPdbImport cenv)
                  Parent = GenPdbImports cenv ilImports.Parent }
            cenv.pdbImports[ilImports] <- v
            Some v

let GenILMethodBody mname cenv env (il: ILMethodBody) =
    let localSigs =
      if cenv.generatePdb then
        il.Locals |> List.toArray |> Array.map (fun l ->
            // Write a fake entry for the local signature headed by e_IMAGE_CEE_CS_CALLCONV_FIELD. This is referenced by the PDB file
            ignore (FindOrAddSharedRow cenv TableNames.StandAloneSig (SharedRow [| Blob (GetFieldDefTypeAsBlobIdx cenv env l.Type) |]))
            // Now write the type
            GetTypeOfLocalAsBytes cenv env l)
      else
        [| |]

    let imports = GenPdbImports cenv il.DebugImports
    let requiredStringFixups, seh, code, seqpoints, scopes = Codebuf.EmitMethodCode cenv imports localSigs env mname il.Code
    let codeSize = code.Length
    use methbuf = ByteBuffer.Create (codeSize * 3)
    // Do we use the tiny format?
    if isNil il.Locals && il.MaxStack <= 8 && isNil seh && codeSize < 64 then
        // Use Tiny format
        let alignedCodeSize = align 4 (codeSize + 1)
        let codePadding = (alignedCodeSize - (codeSize + 1))
        let requiredStringFixups' = (1, requiredStringFixups)
        methbuf.EmitByte (byte codeSize <<< 2 ||| e_CorILMethod_TinyFormat)
        methbuf.EmitBytes code
        methbuf.EmitPadding codePadding
        0x0, (requiredStringFixups', methbuf.AsMemory().ToArray()), seqpoints, scopes
    else
        // Use Fat format
        let flags =
            e_CorILMethod_FatFormat |||
            (if seh <> [] then e_CorILMethod_MoreSects else 0x0uy) |||
            (if il.IsZeroInit then e_CorILMethod_InitLocals else 0x0uy)

        let localToken =
            if isNil il.Locals then 0x0 else
            getUncodedToken TableNames.StandAloneSig
              (FindOrAddSharedRow cenv TableNames.StandAloneSig (GetLocalSigAsStandAloneSigIdx cenv env il.Locals))

        let alignedCodeSize = align 0x4 codeSize
        let codePadding = (alignedCodeSize - codeSize)

        methbuf.EmitByte flags
        methbuf.EmitByte 0x30uy // last four bits record size of fat header in 4 byte chunks - this is always 12 bytes = 3 four word chunks
        methbuf.EmitUInt16 (uint16 il.MaxStack)
        methbuf.EmitInt32 codeSize
        methbuf.EmitInt32 localToken
        methbuf.EmitBytes code
        methbuf.EmitPadding codePadding

        if not (isNil seh) then
            // Can we use the small exception handling table format?
            let smallSize = (seh.Length * 12 + 4)
            let canUseSmall =
              smallSize <= 0xFF &&
              seh |> List.forall (fun (st1, sz1, st2, sz2, _) ->
                  st1 <= 0xFFFF && st2 <= 0xFFFF && sz1 <= 0xFF && sz2 <= 0xFF)

            let kindAsInt32 k =
              match k with
              | FinallyClause -> e_COR_ILEXCEPTION_CLAUSE_FINALLY
              | FaultClause -> e_COR_ILEXCEPTION_CLAUSE_FAULT
              | FilterClause _ -> e_COR_ILEXCEPTION_CLAUSE_FILTER
              | TypeFilterClause _ -> e_COR_ILEXCEPTION_CLAUSE_EXCEPTION
            let kindAsExtraInt32 k =
              match k with
              | FinallyClause | FaultClause -> 0x0
              | FilterClause i -> i
              | TypeFilterClause uncoded -> uncoded

            if canUseSmall then
                methbuf.EmitByte e_CorILMethod_Sect_EHTable
                methbuf.EmitByte (b0 smallSize)
                methbuf.EmitByte 0x00uy
                methbuf.EmitByte 0x00uy
                seh |> List.iter (fun (st1, sz1, st2, sz2, kind) ->
                    let k32 = kindAsInt32 kind
                    methbuf.EmitInt32AsUInt16 k32
                    methbuf.EmitInt32AsUInt16 st1
                    methbuf.EmitByte (b0 sz1)
                    methbuf.EmitInt32AsUInt16 st2
                    methbuf.EmitByte (b0 sz2)
                    methbuf.EmitInt32 (kindAsExtraInt32 kind))
            else
                let bigSize = (seh.Length * 24 + 4)
                methbuf.EmitByte (e_CorILMethod_Sect_EHTable ||| e_CorILMethod_Sect_FatFormat)
                methbuf.EmitByte (b0 bigSize)
                methbuf.EmitByte (b1 bigSize)
                methbuf.EmitByte (b2 bigSize)
                seh |> List.iter (fun (st1, sz1, st2, sz2, kind) ->
                    let k32 = kindAsInt32 kind
                    methbuf.EmitInt32 k32
                    methbuf.EmitInt32 st1
                    methbuf.EmitInt32 sz1
                    methbuf.EmitInt32 st2
                    methbuf.EmitInt32 sz2
                    methbuf.EmitInt32 (kindAsExtraInt32 kind))

        let requiredStringFixups' = (12, requiredStringFixups)

        localToken, (requiredStringFixups', methbuf.AsMemory().ToArray()), seqpoints, scopes

// --------------------------------------------------------------------
// ILFieldDef --> FieldDef Row
// --------------------------------------------------------------------

let rec GetFieldDefAsFieldDefRow cenv env (fd: ILFieldDef) =
    let flags = int fd.Attributes
    UnsharedRow
        [| UShort (uint16 flags)
           StringE (GetStringHeapIdx cenv fd.Name)
           Blob (GetFieldDefSigAsBlobIdx cenv env fd ) |]

and GetFieldDefSigAsBlobIdx cenv env fd = GetFieldDefTypeAsBlobIdx cenv env fd.FieldType

and GenFieldDefPass3 tdef cenv env fd =
    if canGenFieldDef tdef cenv fd then
        let fidx = AddUnsharedRow cenv TableNames.Field (GetFieldDefAsFieldDefRow cenv env fd)
        GenCustomAttrsPass3Or4 cenv (hca_FieldDef, fidx) fd.CustomAttrs
        // Write FieldRVA table - fixups into data section done later
        match fd.Data with
        | None -> ()
        | Some b ->
            let offs = cenv.data.Position
            cenv.data.EmitBytes b
            AddUnsharedRow cenv TableNames.FieldRVA
                (UnsharedRow [| Data (offs, false); SimpleIndex (TableNames.Field, fidx) |]) |> ignore
        // Write FieldMarshal table
        match fd.Marshal with
        | None -> ()
        | Some ntyp ->
            AddUnsharedRow cenv TableNames.FieldMarshal
                  (UnsharedRow [| HasFieldMarshal (hfm_FieldDef, fidx)
                                  Blob (GetNativeTypeAsBlobIdx cenv ntyp) |]) |> ignore
        // Write Content table
        match fd.LiteralValue with
        | None -> ()
        | Some i ->
            AddUnsharedRow cenv TableNames.Constant
                  (UnsharedRow
                      [| GetFieldInitFlags i
                         HasConstant (hc_FieldDef, fidx)
                         Blob (GetFieldInitAsBlobIdx cenv i) |]) |> ignore
        // Write FieldLayout table
        match fd.Offset with
        | None -> ()
        | Some offset ->
            AddUnsharedRow cenv TableNames.FieldLayout
                  (UnsharedRow [| ULong offset; SimpleIndex (TableNames.Field, fidx) |]) |> ignore


// --------------------------------------------------------------------
// ILGenericParameterDef --> GenericParam Row
// --------------------------------------------------------------------

let rec GetGenericParamAsGenericParamRow cenv _env idx owner gp =
    let flags =
        (match gp.Variance with
           | NonVariant -> 0x0000
           | CoVariant -> 0x0001
           | ContraVariant -> 0x0002) |||
        (if gp.HasReferenceTypeConstraint then 0x0004 else 0x0000) |||
        (if gp.HasNotNullableValueTypeConstraint then 0x0008 else 0x0000) |||
        (if gp.HasDefaultConstructorConstraint then 0x0010 else 0x0000) |||
        (if gp.HasAllowsRefStruct then 0x0020 else 0x0000)
   

    let mdVersionMajor, _ = metadataSchemaVersionSupportedByCLRVersion cenv.desiredMetadataVersion
    if (mdVersionMajor = 1) then
        SharedRow
            [| UShort (uint16 idx)
               UShort (uint16 flags)
               TypeOrMethodDef (fst owner, snd owner)
               StringE (GetStringHeapIdx cenv gp.Name)
               TypeDefOrRefOrSpec (tdor_TypeDef, 0) (* empty kind field in deprecated metadata *) |]
    else
        SharedRow
            [| UShort (uint16 idx)
               UShort (uint16 flags)
               TypeOrMethodDef (fst owner, snd owner)
               StringE (GetStringHeapIdx cenv gp.Name) |]

and GenTypeAsGenericParamConstraintRow cenv env gpidx ty =
    let tdorTag, tdorRow = GetTypeAsTypeDefOrRef cenv env ty
    UnsharedRow
        [| SimpleIndex (TableNames.GenericParam, gpidx)
           TypeDefOrRefOrSpec (tdorTag, tdorRow) |]

and GenGenericParamConstraintPass4 cenv env gpidx ty =
    AddUnsharedRow cenv TableNames.GenericParamConstraint (GenTypeAsGenericParamConstraintRow cenv env gpidx ty) |> ignore

and GenGenericParamPass3 cenv env idx owner gp =
    // here we just collect generic params, its constraints\custom attributes will be processed on pass4
    // shared since we look it up again below in GenGenericParamPass4
    AddSharedRow cenv TableNames.GenericParam (GetGenericParamAsGenericParamRow cenv env idx owner gp)
    |> ignore


and GenGenericParamPass4 cenv env idx owner gp =
    let gpidx = FindOrAddSharedRow cenv TableNames.GenericParam (GetGenericParamAsGenericParamRow cenv env idx owner gp)
    GenCustomAttrsPass3Or4 cenv (hca_GenericParam, gpidx) gp.CustomAttrs
    gp.Constraints |> List.iter (GenGenericParamConstraintPass4 cenv env gpidx)

// --------------------------------------------------------------------
// param and return --> Param Row
// --------------------------------------------------------------------

let rec GetParamAsParamRow cenv _env seq (param: ILParameter) =
    let flags =
        (if param.IsIn then 0x0001 else 0x0000) |||
        (if param.IsOut then 0x0002 else 0x0000) |||
        (if param.IsOptional then 0x0010 else 0x0000) |||
        (if param.Default <> None then 0x1000 else 0x0000) |||
        (if param.Marshal <> None then 0x2000 else 0x0000)

    UnsharedRow
        [| UShort (uint16 flags)
           UShort (uint16 seq)
           StringE (GetStringHeapIdxOption cenv param.Name) |]

and GenParamPass3 cenv env seq (param: ILParameter) =
    if not param.IsIn && not param.IsOut && not param.IsOptional && Option.isNone param.Default && Option.isNone param.Name && Option.isNone param.Marshal
    then ()
    else
      let pidx = AddUnsharedRow cenv TableNames.Param (GetParamAsParamRow cenv env seq param)
      GenCustomAttrsPass3Or4 cenv (hca_ParamDef, pidx) param.CustomAttrs
      // Write FieldRVA table - fixups into data section done later
      match param.Marshal with
      | None -> ()
      | Some ntyp ->
          AddUnsharedRow cenv TableNames.FieldMarshal
                (UnsharedRow [| HasFieldMarshal (hfm_ParamDef, pidx); Blob (GetNativeTypeAsBlobIdx cenv ntyp) |]) |> ignore
      // Write Content table for DefaultParameterValue attr
      match param.Default with
      | None -> ()
      | Some i ->
        AddUnsharedRow cenv TableNames.Constant
              (UnsharedRow
                  [| GetFieldInitFlags i
                     HasConstant (hc_ParamDef, pidx)
                     Blob (GetFieldInitAsBlobIdx cenv i) |]) |> ignore

let GenReturnAsParamRow (returnv : ILReturn) =
    let flags = (if returnv.Marshal <> None then 0x2000 else 0x0000)
    UnsharedRow
        [| UShort (uint16 flags)
           UShort 0us (* sequence num. *)
           StringE 0 |]

let GenReturnPass3 cenv (returnv: ILReturn) =
    if Option.isSome returnv.Marshal || not (Array.isEmpty (returnv.CustomAttrs.AsArray())) then
        let pidx = AddUnsharedRow cenv TableNames.Param (GenReturnAsParamRow returnv)
        GenCustomAttrsPass3Or4 cenv (hca_ParamDef, pidx) returnv.CustomAttrs
        match returnv.Marshal with
        | None -> ()
        | Some ntyp ->
            AddUnsharedRow cenv TableNames.FieldMarshal
                (UnsharedRow
                    [| HasFieldMarshal (hfm_ParamDef, pidx)
                       Blob (GetNativeTypeAsBlobIdx cenv ntyp) |]) |> ignore

// --------------------------------------------------------------------
// ILMethodDef --> ILMethodDef Row
// --------------------------------------------------------------------

let GetMethodDefSigAsBytes cenv env (mdef: ILMethodDef) =
    emitBytesViaBuffer (fun bb ->
      bb.EmitByte (callconvToByte mdef.GenericParams.Length mdef.CallingConv)
      if not (List.isEmpty mdef.GenericParams) then bb.EmitZ32 mdef.GenericParams.Length
      bb.EmitZ32 mdef.Parameters.Length
      EmitType cenv env bb mdef.Return.Type
      mdef.ParameterTypes |> List.iter (EmitType cenv env bb))

let GenMethodDefSigAsBlobIdx cenv env mdef =
    GetBytesAsBlobIdx cenv (GetMethodDefSigAsBytes cenv env mdef)

let ilMethodBodyThrowNull =
    let ilCode = IL.buildILCode "" (Dictionary()) [|ILInstr.AI_ldnull; ILInstr.I_throw|] [] []
    mkILMethodBody(false, ILLocals.Empty, 0, ilCode, None, None)

let GenMethodDefAsRow cenv env midx (mdef: ILMethodDef) =
    let flags = mdef.Attributes

    let implflags = mdef.ImplAttributes

    if mdef.IsEntryPoint then
        if cenv.entrypoint <> None then failwith "duplicate entrypoint"
        else cenv.entrypoint <- Some (true, midx)
    let codeAddr =
      (match mdef.Body with
      | MethodBody.IL ilmbodyLazy ->
          let ilmbody =
            if cenv.referenceAssemblyOnly then
                ilMethodBodyThrowNull
            else
                ilmbodyLazy.Value
          let addr = cenv.nextCodeAddr
          let localToken, code, seqpoints, rootScope = GenILMethodBody mdef.Name cenv env ilmbody

          // Now record the PDB record for this method - we write this out later.
          if cenv.generatePdb then
            cenv.pdbinfo.Add
              { MethToken=getUncodedToken TableNames.Method midx
                MethName=mdef.Name
                LocalSignatureToken=localToken
                Params= [| |] (* REVIEW *)
                RootScope = Some rootScope
                DebugRange =
                  match ilmbody.DebugRange with
                  | Some m when cenv.generatePdb ->
                      // table indexes are 1-based, document array indexes are 0-based
                      let doc = (cenv.documents.FindOrAddSharedEntry m.Document) - 1

                      Some ({ Document=doc
                              Line=m.Line
                              Column=m.Column },
                            { Document=doc
                              Line=m.EndLine
                              Column=m.EndColumn })
                  | _ -> None
                DebugPoints=seqpoints }
          cenv.AddCode code
          addr
      | MethodBody.Abstract
      | MethodBody.PInvoke _ ->
          // Now record the PDB record for this method - we write this out later.
          if cenv.generatePdb then
            cenv.pdbinfo.Add
              { MethToken = getUncodedToken TableNames.Method midx
                MethName = mdef.Name
                LocalSignatureToken = 0x0                   // No locals it's abstract
                Params = [| |]
                RootScope = None
                DebugRange = None
                DebugPoints = [| |] }
          0x0000
      | MethodBody.Native ->
          failwith "cannot write body of native method - Abstract IL cannot roundtrip mixed native/managed binaries"
      | _ -> 0x0000)

    UnsharedRow
       [| ULong codeAddr
          UShort (uint16 implflags)
          UShort (uint16 flags)
          StringE (GetStringHeapIdx cenv mdef.Name)
          Blob (GenMethodDefSigAsBlobIdx cenv env mdef)
          SimpleIndex(TableNames.Param, cenv.GetTable(TableNames.Param).Count + 1) |]

let GenMethodImplPass3 cenv env _tgparams tidx mimpl =
    let midxTag, midxRow = GetMethodSpecAsMethodDef cenv env (mimpl.OverrideBy, None)
    let midx2Tag, midx2Row = GetOverridesSpecAsMethodDefOrRef cenv env mimpl.Overrides
    AddUnsharedRow cenv TableNames.MethodImpl
        (UnsharedRow
            [|  SimpleIndex (TableNames.TypeDef, tidx)
                MethodDefOrRef (midxTag, midxRow)
                MethodDefOrRef (midx2Tag, midx2Row) |]) |> ignore

let GenMethodDefPass3 tdef cenv env (mdef: ILMethodDef) =
    if canGenMethodDef tdef cenv mdef then
        let midx = GetMethodDefIdx cenv mdef
        let idx2 = AddUnsharedRow cenv TableNames.Method (GenMethodDefAsRow cenv env midx mdef)
        if midx <> idx2 then failwith "index of method def on pass 3 does not match index on pass 2"
        GenReturnPass3 cenv mdef.Return
        mdef.Parameters |> List.iteri (fun n param -> GenParamPass3 cenv env (n+1) param)
        mdef.CustomAttrs |> GenCustomAttrsPass3Or4 cenv (hca_MethodDef, midx)
        mdef.SecurityDecls.AsList() |> GenSecurityDeclsPass3 cenv (hds_MethodDef, midx)
        mdef.GenericParams |> List.iteri (fun n gp -> GenGenericParamPass3 cenv env n (tomd_MethodDef, midx) gp)
        match mdef.Body with
        | MethodBody.PInvoke attrLazy ->
            let attr = attrLazy.Value
            let flags =
              begin match attr.CallingConv with
              | PInvokeCallingConvention.None -> 0x0000
              | PInvokeCallingConvention.Cdecl -> 0x0200
              | PInvokeCallingConvention.Stdcall -> 0x0300
              | PInvokeCallingConvention.Thiscall -> 0x0400
              | PInvokeCallingConvention.Fastcall -> 0x0500
              | PInvokeCallingConvention.WinApi -> 0x0100
              end |||
              begin match attr.CharEncoding with
              | PInvokeCharEncoding.None -> 0x0000
              | PInvokeCharEncoding.Ansi -> 0x0002
              | PInvokeCharEncoding.Unicode -> 0x0004
              | PInvokeCharEncoding.Auto -> 0x0006
              end |||
              begin match attr.CharBestFit with
              | PInvokeCharBestFit.UseAssembly -> 0x0000
              | PInvokeCharBestFit.Enabled -> 0x0010
              | PInvokeCharBestFit.Disabled -> 0x0020
              end |||
              begin match attr.ThrowOnUnmappableChar with
              | PInvokeThrowOnUnmappableChar.UseAssembly -> 0x0000
              | PInvokeThrowOnUnmappableChar.Enabled -> 0x1000
              | PInvokeThrowOnUnmappableChar.Disabled -> 0x2000
              end |||
              (if attr.NoMangle then 0x0001 else 0x0000) |||
              (if attr.LastError then 0x0040 else 0x0000)
            AddUnsharedRow cenv TableNames.ImplMap
                (UnsharedRow
                   [| UShort (uint16 flags)
                      MemberForwarded (mf_MethodDef, midx)
                      StringE (GetStringHeapIdx cenv attr.Name)
                      SimpleIndex (TableNames.ModuleRef, GetModuleRefAsIdx cenv attr.Where) |]) |> ignore
        | _ -> ()

let GenMethodDefPass4 tdef cenv env mdef =
    if canGenMethodDef tdef cenv mdef then
        let midx = GetMethodDefIdx cenv mdef
        List.iteri (fun n gp -> GenGenericParamPass4 cenv env n (tomd_MethodDef, midx) gp) mdef.GenericParams

let GenPropertyMethodSemanticsPass3 cenv pidx kind mref =
    match TryGetMethodRefAsMethodDefIdx cenv mref with
    | Ok midx ->
        AddUnsharedRow cenv TableNames.MethodSemantics
            (UnsharedRow
               [| UShort (uint16 kind)
                  SimpleIndex (TableNames.Method, midx)
                  HasSemantics (hs_Property, pidx) |]) |> ignore
    | _ -> ()

let rec GetPropertySigAsBlobIdx cenv env prop =
    GetBytesAsBlobIdx cenv (GetPropertySigAsBytes cenv env prop)

and GetPropertySigAsBytes cenv env (prop: ILPropertyDef) =
    emitBytesViaBuffer (fun bb ->
        let b = ((hasthisToByte prop.CallingConv) ||| e_IMAGE_CEE_CS_CALLCONV_PROPERTY)
        bb.EmitByte b
        bb.EmitZ32 prop.Args.Length
        EmitType cenv env bb prop.PropertyType
        prop.Args |> List.iter (EmitType cenv env bb))

and GetPropertyAsPropertyRow cenv env (prop: ILPropertyDef) =
    let flags = prop.Attributes
    UnsharedRow
       [| UShort (uint16 flags)
          StringE (GetStringHeapIdx cenv prop.Name)
          Blob (GetPropertySigAsBlobIdx cenv env prop) |]

/// ILPropertyDef --> Property Row + MethodSemantics entries
and GenPropertyPass3 cenv env (prop: ILPropertyDef) =
    if canGenPropertyDef cenv prop then
        // REVIEW: We do double check here (via canGenerateProperty and GenPropertyMethodSemanticsPass3).
        let pidx = AddUnsharedRow cenv TableNames.Property (GetPropertyAsPropertyRow cenv env prop)
        prop.SetMethod |> Option.iter (GenPropertyMethodSemanticsPass3 cenv pidx 0x0001)
        prop.GetMethod |> Option.iter (GenPropertyMethodSemanticsPass3 cenv pidx 0x0002)
        // Write Constant table
        match prop.Init with
        | None -> ()
        | Some i ->
            AddUnsharedRow cenv TableNames.Constant
                (UnsharedRow
                    [| GetFieldInitFlags i
                       HasConstant (hc_Property, pidx)
                       Blob (GetFieldInitAsBlobIdx cenv i) |]) |> ignore
        GenCustomAttrsPass3Or4 cenv (hca_Property, pidx) prop.CustomAttrs

let rec GenEventMethodSemanticsPass3 cenv eidx kind mref =
    let addIdx = try GetMethodRefAsMethodDefIdx cenv mref with MethodDefNotFound -> 1
    AddUnsharedRow cenv TableNames.MethodSemantics
        (UnsharedRow
            [| UShort (uint16 kind)
               SimpleIndex (TableNames.Method, addIdx)
               HasSemantics (hs_Event, eidx) |]) |> ignore

/// ILEventDef --> Event Row + MethodSemantics entries
and GenEventAsEventRow cenv env (edef: ILEventDef) =
    let flags = edef.Attributes
    let tdorTag, tdorRow = GetTypeOptionAsTypeDefOrRef cenv env edef.EventType
    UnsharedRow
       [| UShort (uint16 flags)
          StringE (GetStringHeapIdx cenv edef.Name)
          TypeDefOrRefOrSpec (tdorTag, tdorRow) |]

and GenEventPass3 cenv env (edef: ILEventDef) =
    if canGenEventDef cenv edef then
        let eidx = AddUnsharedRow cenv TableNames.Event (GenEventAsEventRow cenv env edef)
        edef.AddMethod |> GenEventMethodSemanticsPass3 cenv eidx 0x0008
        edef.RemoveMethod |> GenEventMethodSemanticsPass3 cenv eidx 0x0010
        Option.iter (GenEventMethodSemanticsPass3 cenv eidx 0x0020) edef.FireMethod
        List.iter (GenEventMethodSemanticsPass3 cenv eidx 0x0004) edef.OtherMethods
        GenCustomAttrsPass3Or4 cenv (hca_Event, eidx) edef.CustomAttrs


// --------------------------------------------------------------------
// resource --> generate ...
// --------------------------------------------------------------------

let rec GetResourceAsManifestResourceRow cenv rdef =
    let data, impl =
        let embedManagedResources (bytes: ReadOnlyByteMemory) =
            // Embedded managed resources must be word-aligned. However resource format is
            // not specified in ECMA. Some mscorlib resources appear to be non-aligned - it seems it doesn't matter..
            let offset = cenv.resources.Position
            let alignedOffset = (align 0x8 offset)
            let pad = alignedOffset - offset
            let resourceSize = bytes.Length
            cenv.resources.EmitPadding pad
            cenv.resources.EmitInt32 resourceSize
            cenv.resources.EmitByteMemory bytes
            Data (alignedOffset, true), (i_File, 0)

        match rdef.Location with
        | ILResourceLocation.Local bytes -> embedManagedResources (bytes.GetByteMemory())
        | ILResourceLocation.File (mref, offset) -> ULong offset, (i_File, GetModuleRefAsFileIdx cenv mref)
        | ILResourceLocation.Assembly aref -> ULong 0x0, (i_AssemblyRef, GetAssemblyRefAsIdx cenv aref)

    UnsharedRow
       [| data
          ULong (match rdef.Access with ILResourceAccess.Public -> 0x01 | ILResourceAccess.Private -> 0x02)
          StringE (GetStringHeapIdx cenv rdef.Name)
          Implementation (fst impl, snd impl) |]

and GenResourcePass3 cenv rdef =
  let idx = AddUnsharedRow cenv TableNames.ManifestResource (GetResourceAsManifestResourceRow cenv rdef)
  GenCustomAttrsPass3Or4 cenv (hca_ManifestResource, idx) rdef.CustomAttrs

// --------------------------------------------------------------------
// ILTypeDef --> generate ILFieldDef, ILMethodDef, ILPropertyDef etc. rows
// --------------------------------------------------------------------

let rec GenTypeDefPass3 enc cenv (tdef: ILTypeDef) =
   try
        let env = envForTypeDef tdef
        let tidx = GetIdxForTypeDef cenv (TdKey(enc, tdef.Name))

        tdef.Implements.Value
        |> List.zip cenv.implementsIdxs[tidx]
        |> List.iter (fun (impIdx, impl) -> GenCustomAttrsPass3Or4 cenv (hca_InterfaceImpl,impIdx) impl.CustomAttrs)

        tdef.Properties.AsList() |> List.iter (GenPropertyPass3 cenv env)
        tdef.Events.AsList() |> List.iter (GenEventPass3 cenv env)
        tdef.Fields.AsList() |> List.iter (GenFieldDefPass3 tdef cenv env)
        tdef.Methods |> Seq.iter (GenMethodDefPass3 tdef cenv env)
        tdef.MethodImpls.AsList() |> List.iter (GenMethodImplPass3 cenv env tdef.GenericParams.Length tidx)
        // ClassLayout entry if needed
        match tdef.Layout with
        | ILTypeDefLayout.Auto -> ()
        | ILTypeDefLayout.Sequential layout | ILTypeDefLayout.Explicit layout ->
            if Option.isSome layout.Pack || Option.isSome layout.Size then
                AddUnsharedRow cenv TableNames.ClassLayout
                    (UnsharedRow
                        [| UShort (defaultArg layout.Pack (uint16 0x0))
                           ULong (defaultArg layout.Size 0x0)
                           SimpleIndex (TableNames.TypeDef, tidx) |]) |> ignore

        tdef.SecurityDecls.AsList() |> GenSecurityDeclsPass3 cenv (hds_TypeDef, tidx)
        tdef.CustomAttrs |> GenCustomAttrsPass3Or4 cenv (hca_TypeDef, tidx)
        tdef.GenericParams |> List.iteri (fun n gp -> GenGenericParamPass3 cenv env n (tomd_TypeDef, tidx) gp)
        tdef.NestedTypes.AsList() |> GenTypeDefsPass3 (enc@[tdef.Name]) cenv
   with exn ->
      failwith ("Error in pass3 for type "+tdef.Name+", error: " + exn.Message)
      reraise()
      raise exn

and GenTypeDefsPass3 enc cenv tdefs =
  List.iter (GenTypeDefPass3 enc cenv) tdefs

/// ILTypeDef --> generate generic params on ILMethodDef: ensures
/// GenericParam table is built sorted by owner.

let rec GenTypeDefPass4 enc cenv (tdef: ILTypeDef) =
   try
        let env = envForTypeDef tdef
        let tidx = GetIdxForTypeDef cenv (TdKey(enc, tdef.Name))
        tdef.Methods |> Seq.iter (GenMethodDefPass4 tdef cenv env)
        List.iteri (fun n gp -> GenGenericParamPass4 cenv env n (tomd_TypeDef, tidx) gp) tdef.GenericParams
        GenTypeDefsPass4 (enc@[tdef.Name]) cenv (tdef.NestedTypes.AsList())
   with exn ->
       failwith ("Error in pass4 for type "+tdef.Name+", error: " + exn.Message)
       reraise()
       raise exn

and GenTypeDefsPass4 enc cenv tdefs =
    List.iter (GenTypeDefPass4 enc cenv) tdefs

let timestamp = absilWriteGetTimeStamp ()

// --------------------------------------------------------------------
// ILExportedTypesAndForwarders --> ILExportedTypeOrForwarder table
// --------------------------------------------------------------------

let rec GenNestedExportedTypePass3 cenv cidx (ce: ILNestedExportedType) =
    let flags = GetMemberAccessFlags ce.Access
    let nidx =
      AddUnsharedRow cenv TableNames.ExportedType
        (UnsharedRow
            [| ULong flags
               ULong 0x0
               StringE (GetStringHeapIdx cenv ce.Name)
               StringE 0
               Implementation (i_ExportedType, cidx) |])
    GenCustomAttrsPass3Or4 cenv (hca_ExportedType, nidx) ce.CustomAttrs
    GenNestedExportedTypesPass3 cenv nidx ce.Nested

and GenNestedExportedTypesPass3 cenv nidx (nce: ILNestedExportedTypes) =
    nce.AsList() |> List.iter (GenNestedExportedTypePass3 cenv nidx)

and GenExportedTypePass3 cenv (ce: ILExportedTypeOrForwarder) =
    let nselem, nelem = GetTypeNameAsElemPair cenv ce.Name
    let flags = int32 ce.Attributes
    let impl = GetScopeRefAsImplementationElem cenv ce.ScopeRef
    let cidx =
      AddUnsharedRow cenv TableNames.ExportedType
        (UnsharedRow
            [| ULong flags
               ULong 0x0
               nelem
               nselem
               Implementation (fst impl, snd impl) |])
    GenCustomAttrsPass3Or4 cenv (hca_ExportedType, cidx) ce.CustomAttrs
    GenNestedExportedTypesPass3 cenv cidx ce.Nested

and GenExportedTypesPass3 cenv (ce: ILExportedTypesAndForwarders) =
    List.iter (GenExportedTypePass3 cenv) (ce.AsList())

// --------------------------------------------------------------------
// manifest --> generate Assembly row
// --------------------------------------------------------------------

and GetManifestAsAssemblyRow cenv m =
    UnsharedRow
        [|ULong m.AuxModuleHashAlgorithm
          UShort (match m.Version with None -> 0us | Some version -> version.Major)
          UShort (match m.Version with None -> 0us | Some version -> version.Minor)
          UShort (match m.Version with None -> 0us | Some version -> version.Build)
          UShort (match m.Version with None -> 0us | Some version -> version.Revision)
          ULong
            ( (match m.AssemblyLongevity with
              | ILAssemblyLongevity.Unspecified -> 0x0000
              | ILAssemblyLongevity.Library -> 0x0002
              | ILAssemblyLongevity.PlatformAppDomain -> 0x0004
              | ILAssemblyLongevity.PlatformProcess -> 0x0006
              | ILAssemblyLongevity.PlatformSystem -> 0x0008) |||
              (if m.Retargetable then 0x100 else 0x0) |||
              // Setting these causes peverify errors. Hence both ilread and ilwrite ignore them and refuse to set them.
              // Any debugging customAttributes will automatically propagate
              // REVIEW: No longer appears to be the case
              (if m.JitTracking then 0x8000 else 0x0) |||
              (match m.PublicKey with None -> 0x0000 | Some _ -> 0x0001) ||| 0x0000)
          (match m.PublicKey with None -> Blob 0 | Some x -> Blob (GetBytesAsBlobIdx cenv x))
          StringE (GetStringHeapIdx cenv m.Name)
          (match m.Locale with None -> StringE 0 | Some x -> StringE (GetStringHeapIdx cenv x)) |]

and GenManifestPass3 cenv m =
    let aidx = AddUnsharedRow cenv TableNames.Assembly (GetManifestAsAssemblyRow cenv m)
    GenSecurityDeclsPass3 cenv (hds_Assembly, aidx) (m.SecurityDecls.AsList())
    GenCustomAttrsPass3Or4 cenv (hca_Assembly, aidx) m.CustomAttrs
    GenExportedTypesPass3 cenv m.ExportedTypes
    // Record the entrypoint decl if needed.
    match m.EntrypointElsewhere with
    | Some mref ->
        if cenv.entrypoint <> None then failwith "duplicate entrypoint"
        else cenv.entrypoint <- Some (false, GetModuleRefAsIdx cenv mref)
    | None -> ()

and newGuid (modul: ILModuleDef) =
    let n = timestamp
    let m = hash n
    let m2 = hash modul.Name
    [| b0 m; b1 m; b2 m; b3 m; b0 m2; b1 m2; b2 m2; b3 m2; 0xa7uy; 0x45uy; 0x03uy; 0x83uy; b0 n; b1 n; b2 n; b3 n |]

and deterministicGuid (modul: ILModuleDef) =
    let n = 16909060
    let m2 = Seq.sum (Seq.mapi (fun i x -> i + int x) modul.Name) // use a stable hash
    [| b0 n; b1 n; b2 n; b3 n; b0 m2; b1 m2; b2 m2; b3 m2; 0xa7uy; 0x45uy; 0x03uy; 0x83uy; b0 n; b1 n; b2 n; b3 n |]

and GetModuleAsRow (cenv: cenv) (modul: ILModuleDef) =
    // Store the generated MVID in the environment (needed for generating debug information)
    let modulGuid = if cenv.deterministic then deterministicGuid modul else newGuid modul
    cenv.moduleGuid <- modulGuid
    UnsharedRow
        [| UShort (uint16 0x0)
           StringE (GetStringHeapIdx cenv modul.Name)
           Guid (GetGuidIdx cenv modulGuid)
           Guid 0
           Guid 0 |]


let rowElemCompare (e1: RowElement) (e2: RowElement) =
    let c = compare e1.Val e2.Val
    if c <> 0 then c else
    compare e1.Tag e2.Tag

let TableRequiresSorting tab =
    List.memAssoc tab sortedTableInfo

let SortTableRows tab (rows: GenericRow[]) =
    assert (TableRequiresSorting tab)
    let col = List.assoc tab sortedTableInfo
    rows
        // This needs to be a stable sort, so we use List.sortWith
        |> Array.toList
        |> List.sortWith (fun r1 r2 -> rowElemCompare r1[col] r2[col])
        |> Array.ofList
        //|> Array.map SharedRow

let GenModule (cenv : cenv) (modul: ILModuleDef) =
    let midx = AddUnsharedRow cenv TableNames.Module (GetModuleAsRow cenv modul)
    List.iter (GenResourcePass3 cenv) (modul.Resources.AsList())
    let tdefs = destTypeDefsWithGlobalFunctionsFirst cenv.ilg modul.TypeDefs
    reportTime "Module Generation Preparation"
    GenTypeDefsPass1 [] cenv tdefs
    reportTime "Module Generation Pass 1"
    GenTypeDefsPass2 0 [] cenv tdefs
    reportTime "Module Generation Pass 2"
    (match modul.Manifest with None -> () | Some m -> GenManifestPass3 cenv m)
    GenTypeDefsPass3 [] cenv tdefs
    reportTime "Module Generation Pass 3"
    GenCustomAttrsPass3Or4 cenv (hca_Module, midx) modul.CustomAttrs
    // GenericParam is the only sorted table indexed by Columns in other tables (GenericParamConstraint\CustomAttributes).
    // Hence we need to sort it before we emit any entries in GenericParamConstraint\CustomAttributes that are attached to generic params.
    // Note this mutates the rows in a table. 'SetRowsOfTable' clears
    // the key --> index map since it is no longer valid
    cenv.GetTable(TableNames.GenericParam).SetRowsOfSharedTable (SortTableRows TableNames.GenericParam (cenv.GetTable(TableNames.GenericParam).GenericRowsOfTable))
    GenTypeDefsPass4 [] cenv tdefs
    reportTime "Module Generation Pass 4"

/// Arbitrary value
[<Literal>]
let CodeChunkCapacity = 40000
/// Arbitrary value
[<Literal>]
let DataCapacity = 200
/// Arbitrary value
[<Literal>]
let ResourceCapacity = 200

let generateIL (
    requiredDataFixups,
    desiredMetadataVersion,
    generatePdb,
    ilg: ILGlobals,
    emitTailcalls,
    deterministic,  
    referenceAssemblyOnly,
    referenceAssemblyAttribOpt: ILAttribute option,
    allGivenSources,
    m: ILModuleDef,
    cilStartAddress,
    normalizeAssemblyRefs) =

    let isDll = m.IsDLL

    let hasInternalsVisibleToAttrib =
        (match m.Manifest with Some manifest -> manifest.CustomAttrs | None -> m.CustomAttrs).AsArray()
        |> Array.exists (fun x -> x.Method.DeclaringType.TypeSpec.Name = "System.Runtime.CompilerServices.InternalsVisibleToAttribute")

    let m =
        // Emit System.Runtime.CompilerServices.ReferenceAssemblyAttribute as an assembly-level attribute when generating a reference assembly.
        // Useful for the runtime to know that the assembly is a reference assembly.
        match referenceAssemblyAttribOpt with
        | Some referenceAssemblyAttrib when referenceAssemblyOnly ->
            { m with
                CustomAttrsStored =
                    mkILCustomAttrsReader (fun _ -> Array.append [|referenceAssemblyAttrib|] (m.CustomAttrs.AsArray())) }
        | _ ->
            m

    let tables =
        Array.init 64 (fun i ->
            if (i = TableNames.AssemblyRef.Index ||
                i = TableNames.MemberRef.Index ||
                i = TableNames.ModuleRef.Index ||
                i = TableNames.File.Index ||
                i = TableNames.TypeRef.Index ||
                i = TableNames.TypeSpec.Index ||
                i = TableNames.MethodSpec.Index ||
                i = TableNames.StandAloneSig.Index ||
                i = TableNames.GenericParam.Index) then
                MetadataTable.Shared (MetadataTable<SharedRow>.New ("row table "+string i, EqualityComparer.Default))
            else
                MetadataTable.Unshared (MetadataTable<UnsharedRow>.New ("row table "+string i, EqualityComparer.Default)))
    use cenv =
        { emitTailcalls=emitTailcalls
          deterministic = deterministic         
          ilg = ilg
          desiredMetadataVersion=desiredMetadataVersion
          requiredDataFixups= requiredDataFixups
          requiredStringFixups = []
          codeChunks=ByteBuffer.Create(CodeChunkCapacity, useArrayPool = true)
          nextCodeAddr = cilStartAddress
          data = ByteBuffer.Create DataCapacity
          resources = ByteBuffer.Create ResourceCapacity
          tables= tables
          AssemblyRefs = MetadataTable<_>.New("ILAssemblyRef", EqualityComparer.Default)
          documents=MetadataTable<_>.New("pdbdocs", EqualityComparer.Default)
          trefCache=Dictionary<_, _>(100)
          pdbinfo= ResizeArray<_>(200)
          moduleGuid= Array.zeroCreate 16
          fieldDefs= MetadataTable<_>.New("field defs", EqualityComparer.Default)
          methodDefIdxsByKey = MetadataTable<_>.New("method defs", EqualityComparer.Default)
          // This uses reference identity on ILMethodDef objects
          methodDefIdxs = Dictionary<_, _>(100, HashIdentity.Reference)
          implementsIdxs = Dictionary<_, _>(100, HashIdentity.Structural)
          propertyDefs = MetadataTable<_>.New("property defs", EqualityComparer.Default)
          eventDefs = MetadataTable<_>.New("event defs", EqualityComparer.Default)
          typeDefs = MetadataTable<_>.New("type defs", EqualityComparer.Default)
          entrypoint=None
          generatePdb=generatePdb
          // These must use structural comparison since they are keyed by arrays
          guids=MetadataTable<_>.New("guids", HashIdentity.Structural)
          blobs= MetadataTable<_>.New("blobs", HashIdentity.Structural)
          strings= MetadataTable<_>.New("strings", EqualityComparer.Default)
          userStrings= MetadataTable<_>.New("user strings", EqualityComparer.Default)
          normalizeAssemblyRefs = normalizeAssemblyRefs
          hasInternalsVisibleToAttrib = hasInternalsVisibleToAttrib
          referenceAssemblyOnly = referenceAssemblyOnly
          pdbImports = Dictionary<_, _>(HashIdentity.Reference) }

    // Now the main compilation step
    GenModule cenv m

    for doc in allGivenSources do
        cenv.EmitDebugDocument(doc)

    // .exe files have a .entrypoint instruction. Do not write it to the entrypoint when writing dll.
    let entryPointToken =
        match cenv.entrypoint with
        | Some (epHere, tok) ->
            if isDll then 0x0
            else getUncodedToken (if epHere then TableNames.Method else TableNames.File) tok
        | None ->
            if not isDll then dprintn "warning: no entrypoint specified in executable binary"
            0x0

    let pdbData =
        { EntryPoint= (if isDll then None else Some entryPointToken)
          Timestamp = timestamp
          ModuleID = cenv.moduleGuid
          Documents = cenv.documents.EntriesAsArray
          Methods = cenv.pdbinfo.ToArray()
          TableRowCounts = cenv.tables |> Seq.map(fun t -> t.Count) |> Seq.toArray }

    let idxForNestedTypeDef (tdefs: ILTypeDef list, tdef: ILTypeDef) =
        let enc = tdefs |> List.map (fun tdef -> tdef.Name)
        GetIdxForTypeDef cenv (TdKey(enc, tdef.Name))

    let strings = Array.map Bytes.stringAsUtf8NullTerminated cenv.strings.EntriesAsArray
    let userStrings = cenv.userStrings.EntriesAsArray |> Array.map System.Text.Encoding.Unicode.GetBytes
    let blobs = cenv.blobs.EntriesAsArray
    let guids = cenv.guids.EntriesAsArray
    let tables = cenv.tables
    let code = cenv.GetCode()
    // turn idx tbls into token maps
    let mappings =
     { TypeDefTokenMap = (fun t ->
        getUncodedToken TableNames.TypeDef (idxForNestedTypeDef t))
       FieldDefTokenMap = (fun t fd ->
        let tidx = idxForNestedTypeDef t
        getUncodedToken TableNames.Field (GetFieldDefAsFieldDefIdx cenv tidx fd))
       MethodDefTokenMap = (fun t mdef ->
        let tidx = idxForNestedTypeDef t
        getUncodedToken TableNames.Method (FindMethodDefIdx cenv (GetKeyForMethodDef cenv tidx mdef)))
       PropertyTokenMap = (fun t pdef ->
        let tidx = idxForNestedTypeDef t
        getUncodedToken TableNames.Property (cenv.propertyDefs.GetTableEntry (GetKeyForPropertyDef tidx pdef)))
       EventTokenMap = (fun t edef ->
        let tidx = idxForNestedTypeDef t
        getUncodedToken TableNames.Event (cenv.eventDefs.GetTableEntry (EventKey (tidx, edef.Name)))) }
    reportTime "Finalize Module Generation Results"
    // New return the results
    let data = cenv.data.AsMemory().ToArray()
    let resources = cenv.resources.AsMemory().ToArray()
    (strings, userStrings, blobs, guids, tables, entryPointToken, code, cenv.requiredStringFixups, data, resources, pdbData, mappings)


//=====================================================================
// TABLES+BLOBS --> PHYSICAL METADATA+BLOBS
//=====================================================================
let chunk sz next = ({addr=next; size=sz}, next + sz)
let emptychunk next = ({addr=next; size=0}, next)
let nochunk next = ({addr= 0x0;size= 0x0; }, next)

let count f arr =
    Array.fold (fun x y -> x + f y) 0x0 arr

module FileSystemUtilities =
    open System.Reflection
    open System.Globalization
    let progress = try Environment.GetEnvironmentVariable("FSharp_DebugSetFilePermissions") <> null with _ -> false

/// Arbitrary value
[<Literal>]
let TableCapacity = 20000
/// Arbitrary value
[<Literal>]
let MetadataCapacity = 500000

let writeILMetadataAndCode (
    generatePdb,
    desiredMetadataVersion,
    ilg,
    emitTailcalls,
    deterministic,   
    referenceAssemblyOnly,
    referenceAssemblyAttribOpt,
    allGivenSources,
    modul,
    cilStartAddress,
    normalizeAssemblyRefs
) =

    // When we know the real RVAs of the data section we fixup the references for the FieldRVA table.
    // These references are stored as offsets into the metadata we return from this function
    let requiredDataFixups = ref []

    let next = cilStartAddress

    let strings, userStrings, blobs, guids, tables, entryPointToken, code, requiredStringFixups, data, resources, pdbData, mappings =
      generateIL (
          requiredDataFixups,
          desiredMetadataVersion,
          generatePdb,
          ilg,
          emitTailcalls,
          deterministic,       
          referenceAssemblyOnly,
          referenceAssemblyAttribOpt,
          allGivenSources,
          modul,
          cilStartAddress,
          normalizeAssemblyRefs)

    reportTime "Generated Tables and Code"
    let tableSize (tab: TableName) = tables[tab.Index].Count

   // Now place the code
    let codeSize = code.Length
    let alignedCodeSize = align 0x4 codeSize
    let codep, next = chunk codeSize next
    let codePadding = Array.create (alignedCodeSize - codeSize) 0x0uy
    let _codePaddingChunk, next = chunk codePadding.Length next

   // Now layout the chunks of metadata and IL
    let metadataHeaderStartChunk, _next = chunk 0x10 next

    let numStreams = 0x05

    let mdtableVersionMajor, mdtableVersionMinor = metadataSchemaVersionSupportedByCLRVersion desiredMetadataVersion

    let version =
      System.Text.Encoding.UTF8.GetBytes (sprintf "v%d.%d.%d" desiredMetadataVersion.Major desiredMetadataVersion.Minor desiredMetadataVersion.Build)


    let paddedVersionLength = align 0x4 (Array.length version)

    // Most addresses after this point are measured from the MD root
    // Switch to md-rooted addresses
    let next = metadataHeaderStartChunk.size
    let _metadataHeaderVersionChunk, next = chunk paddedVersionLength next
    let _metadataHeaderEndChunk, next = chunk 0x04 next
    let _tablesStreamHeaderChunk, next = chunk (0x08 + (align 4 ("#~".Length + 0x01))) next
    let _stringsStreamHeaderChunk, next = chunk (0x08 + (align 4 ("#Strings".Length + 0x01))) next
    let _userStringsStreamHeaderChunk, next = chunk (0x08 + (align 4 ("#US".Length + 0x01))) next
    let _guidsStreamHeaderChunk, next = chunk (0x08 + (align 4 ("#GUID".Length + 0x01))) next
    let _blobsStreamHeaderChunk, next = chunk (0x08 + (align 4 ("#Blob".Length + 0x01))) next

    let tablesStreamStart = next

    let stringsStreamUnpaddedSize = count (fun (s: byte[]) -> s.Length) strings + 1
    let stringsStreamPaddedSize = align 4 stringsStreamUnpaddedSize

    let userStringsStreamUnpaddedSize = count (fun (s: byte[]) -> let n = s.Length + 1 in n + ByteBuffer.Z32Size n) userStrings + 1
    let userStringsStreamPaddedSize = align 4 userStringsStreamUnpaddedSize

    let guidsStreamUnpaddedSize = (Array.length guids) * 0x10
    let guidsStreamPaddedSize = align 4 guidsStreamUnpaddedSize

    let blobsStreamUnpaddedSize = count (fun (blob: byte[]) -> let n = blob.Length in n + ByteBuffer.Z32Size n) blobs + 1
    let blobsStreamPaddedSize = align 4 blobsStreamUnpaddedSize

    let guidsBig = guidsStreamPaddedSize >= 0x10000
    let stringsBig = stringsStreamPaddedSize >= 0x10000
    let blobsBig = blobsStreamPaddedSize >= 0x10000

    // 64bit bitvector indicating which tables are in the metadata.
    let (valid1, valid2), _ =
       (((0, 0), 0), tables) ||> Array.fold (fun (valid1, valid2 as valid, n) rows ->
          let valid =
              if rows.Count = 0 then valid else
              ( (if n < 32 then valid1 ||| (1 <<< n ) else valid1),
                (if n >= 32 then valid2 ||| (1 <<< (n-32)) else valid2) )
          (valid, n+1))

    // 64bit bitvector indicating which tables are sorted.
    // Constant - REVIEW: make symbolic! compute from sorted table info!
    let sorted1 = 0x3301fa00
    let sorted2 =
      // If there are any generic parameters in the binary we're emitting then mark that
      // table as sorted, otherwise don't. This maximizes the number of assemblies we emit
      // which have an ECMA-v.1. compliant set of sorted tables.
      (if tableSize TableNames.GenericParam > 0 then 0x00000400 else 0x00000000) |||
      (if tableSize TableNames.GenericParamConstraint > 0 then 0x00001000 else 0x00000000) |||
      0x00000200

    reportTime "Layout Header of Tables"

    let guidAddress n = (if n = 0 then 0 else (n - 1) * 0x10 + 0x01)

    let stringAddressTable =
        let tab = Array.create (strings.Length + 1) 0
        let mutable pos = 1
        for i = 1 to strings.Length do
            tab[i] <- pos
            let s = strings[i - 1]
            pos <- pos + s.Length
        tab

    let stringAddress n =
        if n >= Array.length stringAddressTable then failwith ("string index "+string n+" out of range")
        stringAddressTable[n]

    let userStringAddressTable =
        let tab = Array.create (Array.length userStrings + 1) 0
        let mutable pos = 1
        for i = 1 to Array.length userStrings do
            tab[i] <- pos
            let s = userStrings[i - 1]
            let n = s.Length + 1
            pos <- pos + n + ByteBuffer.Z32Size n
        tab

    let userStringAddress n =
        if n >= Array.length userStringAddressTable then failwith "userString index out of range"
        userStringAddressTable[n]

    let blobAddressTable =
        let tab = Array.create (blobs.Length + 1) 0
        let mutable pos = 1
        for i = 1 to blobs.Length do
            tab[i] <- pos
            let blob = blobs[i - 1]
            pos <- pos + blob.Length + ByteBuffer.Z32Size blob.Length
        tab

    let blobAddress n =
        if n >= blobAddressTable.Length then failwith "blob index out of range"
        blobAddressTable[n]

    reportTime "Build String/Blob Address Tables"

    let sortedTables =
      Array.init 64 (fun i ->
          let tab = tables[i]
          let tabName = TableName.FromIndex i
          let rows = tab.GenericRowsOfTable
          if TableRequiresSorting tabName then SortTableRows tabName rows else rows)

    reportTime "Sort Tables"

    let codedTables =

        let bignessTable = Array.map (fun rows -> Array.length rows >= 0x10000) sortedTables
        let bigness (tab: int32) = bignessTable[tab]

        let codedBigness nbits tab =
          (tableSize tab) >= (0x10000 >>> nbits)

        let tdorBigness =
            codedBigness 2 TableNames.TypeDef ||
            codedBigness 2 TableNames.TypeRef ||
            codedBigness 2 TableNames.TypeSpec

        let tomdBigness =
            codedBigness 1 TableNames.TypeDef ||
            codedBigness 1 TableNames.Method

        let hcBigness =
            codedBigness 2 TableNames.Field ||
            codedBigness 2 TableNames.Param ||
            codedBigness 2 TableNames.Property

        let hcaBigness =
            codedBigness 5 TableNames.Method ||
            codedBigness 5 TableNames.Field ||
            codedBigness 5 TableNames.TypeRef ||
            codedBigness 5 TableNames.TypeDef ||
            codedBigness 5 TableNames.Param ||
            codedBigness 5 TableNames.InterfaceImpl ||
            codedBigness 5 TableNames.MemberRef ||
            codedBigness 5 TableNames.Module ||
            codedBigness 5 TableNames.Permission ||
            codedBigness 5 TableNames.Property ||
            codedBigness 5 TableNames.Event ||
            codedBigness 5 TableNames.StandAloneSig ||
            codedBigness 5 TableNames.ModuleRef ||
            codedBigness 5 TableNames.TypeSpec ||
            codedBigness 5 TableNames.Assembly ||
            codedBigness 5 TableNames.AssemblyRef ||
            codedBigness 5 TableNames.File ||
            codedBigness 5 TableNames.ExportedType ||
            codedBigness 5 TableNames.ManifestResource ||
            codedBigness 5 TableNames.GenericParam ||
            codedBigness 5 TableNames.GenericParamConstraint ||
            codedBigness 5 TableNames.MethodSpec


        let hfmBigness =
            codedBigness 1 TableNames.Field ||
            codedBigness 1 TableNames.Param

        let hdsBigness =
            codedBigness 2 TableNames.TypeDef ||
            codedBigness 2 TableNames.Method ||
            codedBigness 2 TableNames.Assembly

        let mrpBigness =
            codedBigness 3 TableNames.TypeRef ||
            codedBigness 3 TableNames.ModuleRef ||
            codedBigness 3 TableNames.Method ||
            codedBigness 3 TableNames.TypeSpec

        let hsBigness =
            codedBigness 1 TableNames.Event ||
            codedBigness 1 TableNames.Property

        let mdorBigness =
            codedBigness 1 TableNames.Method ||
            codedBigness 1 TableNames.MemberRef

        let mfBigness =
            codedBigness 1 TableNames.Field ||
            codedBigness 1 TableNames.Method

        let iBigness =
            codedBigness 2 TableNames.File ||
            codedBigness 2 TableNames.AssemblyRef ||
            codedBigness 2 TableNames.ExportedType

        let catBigness =
            codedBigness 3 TableNames.Method ||
            codedBigness 3 TableNames.MemberRef

        let rsBigness =
            codedBigness 2 TableNames.Module ||
            codedBigness 2 TableNames.ModuleRef ||
            codedBigness 2 TableNames.AssemblyRef ||
            codedBigness 2 TableNames.TypeRef

        use tablesBuf = ByteBuffer.Create(TableCapacity, useArrayPool = true)

        // Now the coded tables themselves - first the schemata header
        tablesBuf.EmitIntsAsBytes
            [| 0x00; 0x00; 0x00; 0x00
               mdtableVersionMajor // major version of table schemata
               mdtableVersionMinor // minor version of table schemata

               ((if stringsBig then 0x01 else 0x00) |||  // bit vector for heap size
                (if guidsBig then 0x02 else 0x00) |||
                (if blobsBig then 0x04 else 0x00))
               0x01 (* reserved, always 1 *) |]

        tablesBuf.EmitInt32 valid1
        tablesBuf.EmitInt32 valid2
        tablesBuf.EmitInt32 sorted1
        tablesBuf.EmitInt32 sorted2

        // Numbers of rows in various tables
        for rows in sortedTables do
            if rows.Length <> 0 then
                tablesBuf.EmitInt32 rows.Length


        reportTime "Write Header of tablebuf"

      // The tables themselves
        for rows in sortedTables do
            for row in rows do
                for x in row do
                    // Emit the coded token for the array element
                    let t = x.Tag
                    let n = x.Val
                    match t with
                    | _ when t = RowElementTags.UShort -> tablesBuf.EmitUInt16 (uint16 n)
                    | _ when t = RowElementTags.ULong -> tablesBuf.EmitInt32 n
                    | _ when t = RowElementTags.Data -> recordRequiredDataFixup requiredDataFixups tablesBuf (tablesStreamStart + tablesBuf.Position) (n, false)
                    | _ when t = RowElementTags.DataResources -> recordRequiredDataFixup requiredDataFixups tablesBuf (tablesStreamStart + tablesBuf.Position) (n, true)
                    | _ when t = RowElementTags.Guid -> tablesBuf.EmitZUntaggedIndex guidsBig (guidAddress n)
                    | _ when t = RowElementTags.Blob -> tablesBuf.EmitZUntaggedIndex blobsBig (blobAddress n)
                    | _ when t = RowElementTags.String -> tablesBuf.EmitZUntaggedIndex stringsBig (stringAddress n)
                    | _ when t <= RowElementTags.SimpleIndexMax -> tablesBuf.EmitZUntaggedIndex (bigness (t - RowElementTags.SimpleIndexMin)) n
                    | _ when t <= RowElementTags.TypeDefOrRefOrSpecMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.TypeDefOrRefOrSpecMin) 2 tdorBigness n
                    | _ when t <= RowElementTags.TypeOrMethodDefMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.TypeOrMethodDefMin) 1 tomdBigness n
                    | _ when t <= RowElementTags.HasConstantMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.HasConstantMin) 2 hcBigness n
                    | _ when t <= RowElementTags.HasCustomAttributeMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.HasCustomAttributeMin) 5 hcaBigness n
                    | _ when t <= RowElementTags.HasFieldMarshalMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.HasFieldMarshalMin) 1 hfmBigness n
                    | _ when t <= RowElementTags.HasDeclSecurityMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.HasDeclSecurityMin) 2 hdsBigness n
                    | _ when t <= RowElementTags.MemberRefParentMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.MemberRefParentMin) 3 mrpBigness n
                    | _ when t <= RowElementTags.HasSemanticsMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.HasSemanticsMin) 1 hsBigness n
                    | _ when t <= RowElementTags.MethodDefOrRefMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.MethodDefOrRefMin) 1 mdorBigness n
                    | _ when t <= RowElementTags.MemberForwardedMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.MemberForwardedMin) 1 mfBigness n
                    | _ when t <= RowElementTags.ImplementationMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.ImplementationMin) 2 iBigness n
                    | _ when t <= RowElementTags.CustomAttributeTypeMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.CustomAttributeTypeMin) 3 catBigness n
                    | _ when t <= RowElementTags.ResolutionScopeMax -> tablesBuf.EmitZTaggedIndex (t - RowElementTags.ResolutionScopeMin) 2 rsBigness n
                    | _ -> failwith "invalid tag in row element"

        tablesBuf.AsMemory().ToArray()

    reportTime "Write Tables to tablebuf"

    let tablesStreamUnpaddedSize = codedTables.Length
    // QUERY: extra 4 empty bytes in array.exe - why? Include some extra padding after
    // the tables just in case there is a mistake in the ECMA spec.
    let tablesStreamPaddedSize = align 4 (tablesStreamUnpaddedSize + 4)
    let tablesChunk, next = chunk tablesStreamPaddedSize next
    let tablesStreamPadding = tablesChunk.size - tablesStreamUnpaddedSize

    let stringsChunk, next = chunk stringsStreamPaddedSize next
    let stringsStreamPadding = stringsChunk.size - stringsStreamUnpaddedSize
    let userStringsChunk, next = chunk userStringsStreamPaddedSize next
    let userStringsStreamPadding = userStringsChunk.size - userStringsStreamUnpaddedSize
    let guidsChunk, next = chunk (0x10 * guids.Length) next
    let blobsChunk, _next = chunk blobsStreamPaddedSize next
    let blobsStreamPadding = blobsChunk.size - blobsStreamUnpaddedSize

    reportTime "Layout Metadata"

    let metadata, guidStart =
      use mdbuf = ByteBuffer.Create(MetadataCapacity, useArrayPool = true)
      mdbuf.EmitIntsAsBytes
        [| 0x42; 0x53; 0x4a; 0x42 // Magic signature
           0x01; 0x00 // Major version
           0x01; 0x00 // Minor version
        |]
      mdbuf.EmitInt32 0x0 // Reserved

      mdbuf.EmitInt32 paddedVersionLength
      mdbuf.EmitBytes version
      for i = 1 to (paddedVersionLength - Array.length version) do
          mdbuf.EmitIntAsByte 0x00

      mdbuf.EmitBytes
        [| 0x00uy; 0x00uy // flags, reserved
           b0 numStreams; b1 numStreams; |]
      mdbuf.EmitInt32 tablesChunk.addr
      mdbuf.EmitInt32 tablesChunk.size
      mdbuf.EmitIntsAsBytes [| 0x23; 0x7e; 0x00; 0x00; (* #~00 *)|]
      mdbuf.EmitInt32 stringsChunk.addr
      mdbuf.EmitInt32 stringsChunk.size
      mdbuf.EmitIntsAsBytes [| 0x23; 0x53; 0x74; 0x72; 0x69; 0x6e; 0x67; 0x73; 0x00; 0x00; 0x00; 0x00 (* "#Strings0000" *)|]
      mdbuf.EmitInt32 userStringsChunk.addr
      mdbuf.EmitInt32 userStringsChunk.size
      mdbuf.EmitIntsAsBytes [| 0x23; 0x55; 0x53; 0x00; (* #US0*) |]
      mdbuf.EmitInt32 guidsChunk.addr
      mdbuf.EmitInt32 guidsChunk.size
      mdbuf.EmitIntsAsBytes [| 0x23; 0x47; 0x55; 0x49; 0x44; 0x00; 0x00; 0x00; (* #GUID000 *)|]
      mdbuf.EmitInt32 blobsChunk.addr
      mdbuf.EmitInt32 blobsChunk.size
      mdbuf.EmitIntsAsBytes [| 0x23; 0x42; 0x6c; 0x6f; 0x62; 0x00; 0x00; 0x00; (* #Blob000 *)|]

      reportTime "Write Metadata Header"
     // Now the coded tables themselves
      mdbuf.EmitBytes codedTables
      for i = 1 to tablesStreamPadding do
          mdbuf.EmitIntAsByte 0x00
      reportTime "Write Metadata Tables"

     // The string stream
      mdbuf.EmitByte 0x00uy
      for s in strings do
          mdbuf.EmitBytes s
      for i = 1 to stringsStreamPadding do
          mdbuf.EmitIntAsByte 0x00
      reportTime "Write Metadata Strings"
     // The user string stream
      mdbuf.EmitByte 0x00uy
      for s in userStrings do
          mdbuf.EmitZ32 (s.Length + 1)
          mdbuf.EmitBytes s
          mdbuf.EmitIntAsByte (markerForUnicodeBytes s)
      for i = 1 to userStringsStreamPadding do
          mdbuf.EmitIntAsByte 0x00

      reportTime "Write Metadata User Strings"
    // The GUID stream
      let guidStart = mdbuf.Position
      Array.iter mdbuf.EmitBytes guids

    // The blob stream
      mdbuf.EmitByte 0x00uy
      for s in blobs do
          mdbuf.EmitZ32 s.Length
          mdbuf.EmitBytes s
      for i = 1 to blobsStreamPadding do
          mdbuf.EmitIntAsByte 0x00
      reportTime "Write Blob Stream"
     // Done - close the buffer and return the result.
      mdbuf.AsMemory().ToArray(), guidStart


   // Now we know the user string tables etc. we can fixup the
   // uses of strings in the code
    for codeStartAddr, l in requiredStringFixups do
        for codeOffset, userStringIndex in l do
              if codeStartAddr < codep.addr || codeStartAddr >= codep.addr + codep.size then
                  failwith "strings-in-code fixup: a group of fixups is located outside the code array"
              let locInCode = ((codeStartAddr + codeOffset) - codep.addr)
              checkFixup32 code locInCode 0xdeadbeef
              let token = getUncodedToken TableNames.UserStrings (userStringAddress userStringIndex)
              if (Bytes.get code (locInCode-1) <> i_ldstr) then failwith "strings-in-code fixup: not at ldstr instruction!"
              applyFixup32 code locInCode token
    reportTime "Fixup Metadata"

    entryPointToken, code, codePadding, metadata, data, resources, requiredDataFixups.Value, pdbData, mappings, guidStart

//---------------------------------------------------------------------
// PHYSICAL METADATA+BLOBS --> PHYSICAL PE FORMAT
//---------------------------------------------------------------------

// THIS LAYS OUT A 2-SECTION .NET PE BINARY
// SECTIONS
// TEXT: physical 0x0200 --> RVA 0x00020000
//         e.g. raw size 0x9600,
//         e.g. virt size 0x9584
// RELOC: physical 0x9800 --> RVA 0x0000c000
//    i.e. physbase --> rvabase
//    where physbase = textbase + text raw size
//         phsrva = roundup(0x2000, 0x0002000 + text virt size)

let msdosHeader : byte[] =
     [| 0x4duy; 0x5auy; 0x90uy; 0x00uy; 0x03uy; 0x00uy; 0x00uy; 0x00uy
        0x04uy; 0x00uy; 0x00uy; 0x00uy; 0xFFuy; 0xFFuy; 0x00uy; 0x00uy
        0xb8uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy
        0x40uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy
        0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy
        0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy
        0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy
        0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x80uy; 0x00uy; 0x00uy; 0x00uy
        0x0euy; 0x1fuy; 0xbauy; 0x0euy; 0x00uy; 0xb4uy; 0x09uy; 0xcduy
        0x21uy; 0xb8uy; 0x01uy; 0x4cuy; 0xcduy; 0x21uy; 0x54uy; 0x68uy
        0x69uy; 0x73uy; 0x20uy; 0x70uy; 0x72uy; 0x6fuy; 0x67uy; 0x72uy
        0x61uy; 0x6duy; 0x20uy; 0x63uy; 0x61uy; 0x6euy; 0x6euy; 0x6fuy
        0x74uy; 0x20uy; 0x62uy; 0x65uy; 0x20uy; 0x72uy; 0x75uy; 0x6euy
        0x20uy; 0x69uy; 0x6euy; 0x20uy; 0x44uy; 0x4fuy; 0x53uy; 0x20uy
        0x6duy; 0x6fuy; 0x64uy; 0x65uy; 0x2euy; 0x0duy; 0x0duy; 0x0auy
        0x24uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x00uy |]

let writeInt64 (os: BinaryWriter) x =
    os.Write (dw0 x)
    os.Write (dw1 x)
    os.Write (dw2 x)
    os.Write (dw3 x)
    os.Write (dw4 x)
    os.Write (dw5 x)
    os.Write (dw6 x)
    os.Write (dw7 x)

let writeInt32 (os: BinaryWriter) x =
    os.Write (byte (b0 x))
    os.Write (byte (b1 x))
    os.Write (byte (b2 x))
    os.Write (byte (b3 x))

let writeInt32AsUInt16 (os: BinaryWriter) x =
    os.Write (byte (b0 x))
    os.Write (byte (b1 x))

let writeDirectory os dict =
    writeInt32 os (if dict.size = 0x0 then 0x0 else dict.addr)
    writeInt32 os dict.size

let writeBytes (os: BinaryWriter) (chunk: byte[]) = os.Write(chunk, 0, chunk.Length)

let writePdb (
    dumpDebugInfo,   
    embeddedPDB,
    pdbfile,
    outfile,
    reopenOutput,
    writePdbInMemory,
    signer: ILStrongNameSigner option,
    deterministic,
    pathMap,
    pdbData,
    pdbInfoOpt,
    debugDirectoryChunk,
    debugDataChunk,
    debugChecksumPdbChunk,
    debugEmbeddedPdbChunk,
    debugDeterministicPdbChunk,
    textV2P) =

    if dumpDebugInfo then logDebugInfo outfile pdbData

    // Used to capture the pdb file bytes in the case we're generating in-memory
    let mutable pdbBytes = None

    let signImage reopenOutput =
        // Sign the binary. No further changes to binary allowed past this point!
        match signer with
        | None -> ()
        | Some s ->
            use fs = reopenOutput ()
            try
                s.SignStream fs
            with exn ->
                failwith ($"Warning: A call to SignFile failed ({exn.Message})")
        reportTime "Signing Image"

    // Now we've done the bulk of the binary, do the PDB file and fixup the binary.
    match pdbfile with
    | None -> signImage reopenOutput

    | Some pdbfile ->
        let idd =
            match pdbInfoOpt with
            | Some (originalLength, contentId, stream: MemoryStream, algorithmName, checkSum) ->
                if embeddedPDB then
                    getInfoForEmbeddedPortablePdb originalLength contentId stream pdbfile debugDataChunk debugEmbeddedPdbChunk debugDeterministicPdbChunk debugChecksumPdbChunk algorithmName checkSum deterministic
                else
                    if writePdbInMemory then
                        let ms = new MemoryStream()
                        stream.WriteTo ms
                        ms.Close()
                        pdbBytes <- Some (ms.ToArray())
                    else
                        let outfileInfo = FileInfo(outfile).FullName
                        let pdbfileInfo = FileInfo(pdbfile).FullName

                        // If pdbfilepath matches output filepath then error
                        if String.Compare(outfileInfo, pdbfileInfo, StringComparison.InvariantCulture) = 0 then
                            errorR(Error(FSComp.SR.optsPdbMatchesOutputFileName(), rangeStartup))
                        try FileSystem.FileDeleteShim pdbfile with _ -> ()
                        use fs = FileSystem.OpenFileForWriteShim(pdbfile, fileMode = FileMode.Create, fileAccess = FileAccess.ReadWrite)
                        stream.WriteTo fs
                    getInfoForPortablePdb contentId pdbfile pathMap debugDataChunk debugDeterministicPdbChunk debugChecksumPdbChunk algorithmName checkSum embeddedPDB deterministic
            | None -> [| |]

        // Now we have the debug data we can go back and fill in the debug directory in the image
        use fs2 = reopenOutput ()
        let os2 = new BinaryWriter(fs2)
        try
            // write the IMAGE_DEBUG_DIRECTORY
            os2.BaseStream.Seek (int64 (textV2P debugDirectoryChunk.addr), SeekOrigin.Begin) |> ignore
            for i in idd do
                writeInt32 os2 i.iddCharacteristics           // IMAGE_DEBUG_DIRECTORY.Characteristics
                writeInt32 os2 i.iddTimestamp
                writeInt32AsUInt16 os2 i.iddMajorVersion
                writeInt32AsUInt16 os2 i.iddMinorVersion
                writeInt32 os2 i.iddType
                writeInt32 os2 i.iddData.Length               // IMAGE_DEBUG_DIRECTORY.SizeOfData
                writeInt32 os2 i.iddChunk.addr                // IMAGE_DEBUG_DIRECTORY.AddressOfRawData
                writeInt32 os2 (textV2P i.iddChunk.addr)      // IMAGE_DEBUG_DIRECTORY.PointerToRawData

            // Write the Debug Data
            for i in idd do
                if i.iddChunk.size <> 0 then
                    // write the debug raw data as given us by the PDB writer
                    os2.BaseStream.Seek (int64 (textV2P i.iddChunk.addr), SeekOrigin.Begin) |> ignore
                    if i.iddChunk.size < i.iddData.Length then failwith "Debug data area is not big enough. Debug info may not be usable"
                    writeBytes os2 i.iddData
            reportTime "Finalize PDB"

            let gotoStartOfStream (stream:Stream) : Stream =
                stream.Seek(0, SeekOrigin.Begin) |> ignore
                stream

            signImage (fun () -> gotoStartOfStream os2.BaseStream)
            os2.Dispose()
            reportTime "Generate PDB Info"
        with exn ->
            failwith ("Error while writing debug directory entry: " + exn.Message)
            (try os2.Dispose(); FileSystem.FileDeleteShim outfile with _ -> ())
            reraise()
           
    reportTime "Finish"
    pdbBytes

type options =
   { ilg: ILGlobals
     outfile: string
     pdbfile: string option
     portablePDB: bool
     embeddedPDB: bool
     embedAllSource: bool
     embedSourceList: string list
     allGivenSources: ILSourceDocument list
     sourceLink: string
     checksumAlgorithm: HashAlgorithm
     signer: ILStrongNameSigner option
     emitTailcalls: bool
     deterministic: bool  
     dumpDebugInfo: bool
     referenceAssemblyOnly: bool
     referenceAssemblyAttribOpt: ILAttribute option
     referenceAssemblySignatureHash : int option
     pathMap: PathMap }

let writeBinaryAux (stream: Stream, options: options, modul, normalizeAssemblyRefs) =

    // Store the public key from the signer into the manifest. This means it will be written
    // to the binary and also acts as an indicator to leave space for delay sign

    reportTime "Write Started"
    let isDll = modul.IsDLL
    let ilg = options.ilg

    let signer =
        match options.signer, modul.Manifest with
        | Some _, _ -> options.signer
        | _, None -> options.signer
        | None, Some {PublicKey=Some pubkey} ->
            (dprintn "Note: The output assembly will be delay-signed using the original public"
             dprintn "Note: key. In order to load it you will need to either sign it with"
             dprintn "Note: the original private key or to turn off strong-name verification"
             dprintn "Note: (use sn.exe from the .NET Framework SDK to do this, e.g. 'sn -Vr *')."
             dprintn "Note: Alternatively if this tool supports it you can provide the original"
             dprintn "Note: private key when converting the assembly, assuming you have access to"
             dprintn "Note: it."
             Some (ILStrongNameSigner.OpenPublicKey pubkey))
        | _ -> options.signer

    let modul =
        let pubkey =
          match signer with
          | None -> None
          | Some s ->
             try Some s.PublicKey
             with exn ->
               failwith ("A call to StrongNameGetPublicKey failed (" + exn.Message + ")")
               None
        match modul.Manifest with
        | None -> ()
        | Some m ->
           if m.PublicKey <> None && m.PublicKey <> pubkey then
             dprintn "Warning: The output assembly is being signed or delay-signed with a strong name that is different to the original."
        { modul with Manifest = match modul.Manifest with None -> None | Some m -> Some {m with PublicKey = pubkey} }

    let pdbData, pdbInfoOpt, debugDirectoryChunk, debugDataChunk, debugChecksumPdbChunk, debugEmbeddedPdbChunk, debugDeterministicPdbChunk, textV2P, mappings =

          let os = new BinaryWriter(stream, System.Text.Encoding.UTF8)

          let imageBaseReal = modul.ImageBase       // FIXED CHOICE
          let alignVirt = modul.VirtualAlignment    // FIXED CHOICE
          let alignPhys = modul.PhysicalAlignment   // FIXED CHOICE

          let isItanium = modul.Platform = Some IA64
          let isItaniumOrAMD = match modul.Platform with | Some IA64 | Some AMD64 -> true | _ -> false
          let hasEntryPointStub = match modul.Platform with | Some ARM64 | Some ARM -> false | _ -> true
          let hasMvidSection = options.referenceAssemblyAttribOpt.IsSome
          let numSections =
              let ns = if hasMvidSection then 1 else 0
              if hasEntryPointStub then ns + 3           // [.mvid], .text, .sdata, .reloc
              else ns + 2                                // [.mvid], .text, .sdata

          // HEADERS
          let next = 0x0
          let headerSectionPhysLoc = 0x0
          let headerAddr = next
          let next = headerAddr

          let msdosHeaderChunk, next = chunk 0x80 next
          let peSignatureChunk, next = chunk 0x04 next
          let peFileHeaderChunk, next = chunk 0x14 next
          let peOptionalHeaderChunk, next = chunk (if modul.Is64Bit then 0xf0 else 0xe0) next
          let mvidSectionHeaderChunk, next =
              if hasMvidSection then chunk 0x28 next
              else nochunk next
          let textSectionHeaderChunk, next = chunk 0x28 next
          let dataSectionHeaderChunk, next = chunk 0x28 next
          let relocSectionHeaderChunk, next = if hasEntryPointStub then chunk 0x28 next else nochunk next

          let headerSize = next - headerAddr
          let nextPhys = align alignPhys (headerSectionPhysLoc + headerSize)
          let headerSectionPhysSize = nextPhys - headerSectionPhysLoc
          let next = align alignVirt (headerAddr + headerSize)

          // .MVID SECTION
          let mvidSectionPhysLoc = nextPhys
          let mvidSectionAddr =  next
          let mvidSectionChunk, next =  if hasMvidSection then chunk sizeof<Guid> next else nochunk next
          let mvidSectionSize = if hasMvidSection then next - mvidSectionAddr else 0x00
          let nextPhys = if hasMvidSection then align alignPhys (mvidSectionPhysLoc + mvidSectionSize) else nextPhys
          let mvidSectionPhysSize = if hasMvidSection then nextPhys - mvidSectionPhysLoc else 0x00
          let next = if hasMvidSection then align alignVirt (mvidSectionAddr + mvidSectionSize) else align alignVirt next

          // TEXT SECTION: 8 bytes IAT table 72 bytes CLI header
          let textSectionPhysLoc = nextPhys
          let textSectionAddr = next
          let next = textSectionAddr

          // IAT not for ARM
          let importAddrTableChunk, next = if hasEntryPointStub then chunk 0x08 next else nochunk next
          let cliHeaderPadding = (if isItanium then (align 16 next) else next) - next
          let next = next + cliHeaderPadding
          let cliHeaderChunk, next = chunk 0x48 next

          let desiredMetadataVersion =
            if modul.MetadataVersion <> "" then
                parseILVersion modul.MetadataVersion
            else
                match ilg.primaryAssemblyScopeRef with
                | ILScopeRef.Local -> failwith "Expected mscorlib to be ILScopeRef.Assembly was ILScopeRef.Local"
                | ILScopeRef.Module _ -> failwith "Expected mscorlib to be ILScopeRef.Assembly was ILScopeRef.Module"
                | ILScopeRef.PrimaryAssembly -> failwith "Expected mscorlib to be ILScopeRef.Assembly was ILScopeRef.PrimaryAssembly"
                | ILScopeRef.Assembly aref ->
                    match aref.Version with
                    | Some version when version.Major = 2us -> parseILVersion "2.0.50727.0"
                    | Some v -> v
                    | None -> failwith "Expected mscorlib to have a version number"

          let entryPointToken, code, codePadding, metadata, data, resources, requiredDataFixups, pdbData, mappings, guidStart =
              writeILMetadataAndCode (
                  options.pdbfile.IsSome,
                  desiredMetadataVersion,
                  ilg,
                  options.emitTailcalls,
                  options.deterministic,                 
                  options.referenceAssemblyOnly,
                  options.referenceAssemblyAttribOpt,
                  options.allGivenSources,
                  modul,
                  next,
                  normalizeAssemblyRefs
              )

          reportTime "Generated IL and metadata"
          let _codeChunk, next = chunk code.Length next
          let _codePaddingChunk, next = chunk codePadding.Length next

          let metadataChunk, next = chunk metadata.Length next

          let strongnameChunk, next =
            match signer with
            | None -> nochunk next
            | Some s -> chunk s.SignatureSize next

          let resourcesChunk, next = chunk resources.Length next

          let rawdataChunk, next = chunk data.Length next

          let vtfixupsChunk, next = nochunk next   // Note: only needed for mixed mode assemblies
          let importTableChunkPrePadding = (if isItanium then (align 16 next) else next) - next
          let next = next + importTableChunkPrePadding
          let importTableChunk, next = if hasEntryPointStub then chunk 0x28 next else nochunk next
          let importLookupTableChunk, next = if hasEntryPointStub then chunk 0x14 next else nochunk next
          let importNameHintTableChunk, next = if hasEntryPointStub then chunk 0x0e next else nochunk next
          let mscoreeStringChunk, next = if hasEntryPointStub then chunk 0x0c next else nochunk next

          let next = if hasEntryPointStub then align 0x10 (next + 0x05) - 0x05 else next
          let importTableChunk = { addr=importTableChunk.addr; size = next - importTableChunk.addr}
          let importTableChunkPadding = if hasEntryPointStub then importTableChunk.size - (0x28 + 0x14 + 0x0e + 0x0c) else importTableChunk.size
          let entrypointCodeChunk, next = if hasEntryPointStub then chunk 0x06 (next + 0x03) else nochunk next
          let globalpointerCodeChunk, next = chunk (if isItanium then 0x8 else 0x0) next

          let pdbInfoOpt =
            match options.pdbfile, options.portablePDB with
            | Some _, true ->
                let pdbInfo =
                    generatePortablePdb options.embedAllSource options.embedSourceList options.sourceLink options.checksumAlgorithm pdbData options.pathMap

                if options.embeddedPDB then
                    let (uncompressedLength, contentId, stream, algorithmName, checkSum) = pdbInfo
                    let compressedStream = compressPortablePdbStream stream
                    Some (uncompressedLength, contentId, compressedStream, algorithmName, checkSum)
                else
                    Some pdbInfo

            | _ -> None

          let debugDirectoryChunk, next =
            chunk (if options.pdbfile = None then
                        0x0
                   else
                        sizeof_IMAGE_DEBUG_DIRECTORY * 2 +
                        (if options.embeddedPDB then sizeof_IMAGE_DEBUG_DIRECTORY else 0) +
                        (if options.deterministic then sizeof_IMAGE_DEBUG_DIRECTORY else 0)
                  ) next

          // The debug data is given to us by the PDB writer and appears to
          // typically be the type of the data plus the PDB file name. We fill
          // this in after we've written the binary. We approximate the size according
          // to what PDB writers seem to require and leave extra space just in case...
          let debugDataJustInCase = 40
          let debugDataChunk, next =
              chunk (align 0x4 (match options.pdbfile with
                                | None -> 0
                                | Some f -> (24
                                            + System.Text.Encoding.Unicode.GetByteCount f // See bug 748444
                                            + debugDataJustInCase))) next

          let debugChecksumPdbChunk, next =
              chunk (align 0x4 (match pdbInfoOpt with
                                | Some (_, _, _, algorithmName, checkSum) ->
                                    let alg = System.Text.Encoding.UTF8.GetBytes(algorithmName)
                                    let size = alg.Length + 1 + checkSum.Length
                                    size
                                | None -> 0)) next

          let debugEmbeddedPdbChunk, next =
              if options.embeddedPDB then
                  let streamLength =
                      match pdbInfoOpt with
                      | Some (_, _, stream, _, _) -> int stream.Length
                      | None -> 0
                  chunk (align 0x4 (match options.embeddedPDB with
                                     | true -> 8 + streamLength
                                     | _ -> 0 )) next
              else
                  nochunk next

          let debugDeterministicPdbChunk, next =
              if options.deterministic then emptychunk next
              else nochunk next

          let textSectionSize = next - textSectionAddr
          let nextPhys = align alignPhys (textSectionPhysLoc + textSectionSize)
          let textSectionPhysSize = nextPhys - textSectionPhysLoc
          let next = align alignVirt (textSectionAddr + textSectionSize)

          // .RSRC SECTION (DATA)
          let dataSectionPhysLoc = nextPhys
          let dataSectionAddr = next
          let dataSectionVirtToPhys v = v - dataSectionAddr + dataSectionPhysLoc
          let nativeResources =
            match modul.NativeResources with
            | [] -> [||]
            | resources ->
                let unlinkedResources =
                    resources |> List.map (function
                        | ILNativeResource.Out bytes -> bytes
                        | ILNativeResource.In (fileName, linkedResourceBase, start, len) ->
                             use stream = FileSystem.OpenFileForReadShim(fileName)
                             let linkedResource = stream.ReadBytes(start, len)
                             unlinkResource linkedResourceBase linkedResource)

                try linkNativeResources unlinkedResources next
                with exn -> failwith ("Linking a native resource failed: " + exn.Message)

          let nativeResourcesSize = nativeResources.Length

          let nativeResourcesChunk, next = chunk nativeResourcesSize next

          let dummydatap, next = chunk (if next = dataSectionAddr then 0x01 else 0x0) next

          let dataSectionSize = next - dataSectionAddr
          let nextPhys = align alignPhys (dataSectionPhysLoc + dataSectionSize)
          let dataSectionPhysSize = nextPhys - dataSectionPhysLoc
          let next = align alignVirt (dataSectionAddr + dataSectionSize)

          // .RELOC SECTION base reloc table: 0x0c size
          let relocSectionPhysLoc = nextPhys
          let relocSectionAddr =  if hasEntryPointStub then next else 0x00
          let baseRelocTableChunk, next =  if hasEntryPointStub then chunk 0x0c next else nochunk next

          let relocSectionSize = if hasEntryPointStub then next - relocSectionAddr else 0x00
          let nextPhys = if hasEntryPointStub then align alignPhys (relocSectionPhysLoc + relocSectionSize) else nextPhys
          let relocSectionPhysSize = if hasEntryPointStub then nextPhys - relocSectionPhysLoc else 0x00
          let next = if hasEntryPointStub then align alignVirt (relocSectionAddr + relocSectionSize) else align alignVirt next

         // Now we know where the data section lies we can fix up the
         // references into the data section from the metadata tables.
          requiredDataFixups |> List.iter
              (fun (metadataOffset32, (dataOffset, kind)) ->
                let metadataOffset = metadataOffset32
                if metadataOffset < 0 || metadataOffset >= metadata.Length - 4 then failwith "data RVA fixup: fixup located outside metadata"
                checkFixup32 metadata metadataOffset 0xdeaddddd
                let dataRva =
                  if kind then
                      let res = dataOffset
                      if res >= resourcesChunk.size then dprintn "resource offset bigger than resource data section"
                      res
                  else
                      let res = rawdataChunk.addr + dataOffset
                      if res < rawdataChunk.addr then dprintn "data rva before data section"
                      if res >= rawdataChunk.addr + rawdataChunk.size then
                          dprintn ("data rva after end of data section, dataRva = "+string res+", rawdataChunk.addr = "+string rawdataChunk.addr
                                   + ", rawdataChunk.size = "+string rawdataChunk.size)
                      res
                applyFixup32 metadata metadataOffset dataRva)

         // IMAGE TOTAL SIZE
          let imageEndSectionPhysLoc = nextPhys
          let imageEndAddr = next

          reportTime "Layout image"

          let write p (os: BinaryWriter) chunkName chunk =
              match p with
              | None -> ()
              | Some pExpected ->
                  os.Flush()
                  let pCurrent = int32 os.BaseStream.Position
                  if pCurrent <> pExpected then
                    failwith ("warning: "+chunkName+" not where expected, pCurrent = "+string pCurrent+", p.addr = "+string pExpected)
              writeBytes os chunk

          let writePadding (os: BinaryWriter) _comment sz =
              if sz < 0 then failwith "writePadding: size < 0"
              for i = 0 to sz - 1 do
                  os.Write 0uy

          // Now we've computed all the offsets, write the image
          write (Some msdosHeaderChunk.addr) os "msdos header" msdosHeader
          write (Some peSignatureChunk.addr) os "pe signature" [| |]
          writeInt32 os 0x4550
          write (Some peFileHeaderChunk.addr) os "pe file header" [| |]

          match modul.Platform with
          | Some AMD64 -> writeInt32AsUInt16 os 0x8664      // Machine - IMAGE_FILE_MACHINE_AMD64
          | Some IA64 -> writeInt32AsUInt16 os 0x200        // Machine - IMAGE_FILE_MACHINE_IA64
          | Some ARM64 -> writeInt32AsUInt16 os 0xaa64      // Machine - IMAGE_FILE_MACHINE_ARM64
          | Some ARM -> writeInt32AsUInt16 os 0x1c4         // Machine - IMAGE_FILE_MACHINE_ARMNT
          | _ ->  writeInt32AsUInt16 os 0x014c              // Machine - IMAGE_FILE_MACHINE_I386

          writeInt32AsUInt16 os numSections

          use sha =
              match options.checksumAlgorithm with
              | HashAlgorithm.Sha1 -> System.Security.Cryptography.SHA1.Create() :> System.Security.Cryptography.HashAlgorithm
              | HashAlgorithm.Sha256 -> System.Security.Cryptography.SHA256.Create() :> System.Security.Cryptography.HashAlgorithm

          let hCode = sha.ComputeHash code
          let hData = sha.ComputeHash data   
          // Not yet suitable for the mvidsection optimization           

          let deterministicId = 
            [| hCode
               hData
               match options.referenceAssemblyOnly, options.referenceAssemblySignatureHash with
               | true, Some impliedSigHash -> System.BitConverter.GetBytes(impliedSigHash)
               | _ -> sha.ComputeHash metadata |] 
            |> Array.collect id 
            |> sha.ComputeHash
          let deterministicMvid () = deterministicId[0..15]
          let pdbData =
            // Hash code, data and metadata
            if options.deterministic then
              // Confirm we have found the correct data and aren't corrupting the metadata
              if metadata[ guidStart..guidStart+3] <> [| 4uy; 3uy; 2uy; 1uy |] then failwith "Failed to find MVID"
              if metadata[ guidStart+12..guidStart+15] <> [| 4uy; 3uy; 2uy; 1uy |] then failwith "Failed to find MVID"

              // Update MVID guid in metadata
              Array.blit deterministicId 0 metadata guidStart 16

              // Use last 4 bytes for timestamp - High bit set, to stop tool chains becoming confused
              let timestamp = int deterministicId[16] ||| (int deterministicId[17] <<< 8) ||| (int deterministicId[18] <<< 16) ||| (int (deterministicId[19] ||| 128uy) <<< 24)
              writeInt32 os timestamp

              // Update pdbData with new guid and timestamp. Portable and embedded PDBs don't need the ModuleID
              // Full and PdbOnly aren't supported under deterministic builds currently, they rely on non-deterministic Windows native code
              { pdbData with ModuleID = deterministicMvid() ; Timestamp = timestamp }
            else
              writeInt32 os timestamp   // date since 1970
              pdbData

          writeInt32 os 0x00 // Pointer to Symbol Table Always 0
       // 00000090
          writeInt32 os 0x00 // Number of Symbols Always 0
          writeInt32AsUInt16 os peOptionalHeaderChunk.size                      // Format is described below.

          // 64bit: IMAGE_FILE_LARGE_ADDRESS_AWARE
          // 32bit: IMAGE_FILE_32BIT_MACHINE
          let iMachineCharacteristic = match modul.Platform with | Some IA64 | Some AMD64 | Some ARM64 -> 0x20 | _ -> 0x0100

          writeInt32AsUInt16 os ((if isDll then 0x2000 else 0x0000) ||| 0x0002 ||| iMachineCharacteristic)

       // Now comes optional header
          let peOptionalHeaderByte = peOptionalHeaderByteByCLRVersion desiredMetadataVersion

          write (Some peOptionalHeaderChunk.addr) os "pe optional header" [| |]
          if modul.Is64Bit then
              writeInt32AsUInt16 os 0x020B              // Magic number is 0x020B for 64-bit
          else
              writeInt32AsUInt16 os 0x010b              // Always 0x10B (see Section 23.1).
          writeInt32AsUInt16 os peOptionalHeaderByte    // ECMA spec says 6, some binaries, e.g. fscmanaged.exe say 7, Whidbey binaries say 8
          writeInt32 os textSectionPhysSize             // Size of the code (text) section, or the sum of all code sections if there are multiple sections.
        // 000000a0
          writeInt32 os dataSectionPhysSize             // Size of the initialized data section
          writeInt32 os 0x00                            // Size of the uninitialized data section
          writeInt32 os entrypointCodeChunk.addr        // RVA of entry point, needs to point to bytes 0xFF 0x25 followed by the RVA+!0x4000000
          writeInt32 os textSectionAddr                 // e.g. 0x0002000
       // 000000b0
          if modul.Is64Bit then
              writeInt64 os (int64 imageBaseReal)
          else
              writeInt32 os dataSectionAddr             // e.g. 0x0000c000
              writeInt32 os (int32 imageBaseReal)       // Image Base Always 0x400000 (see Section 23.1). - QUERY : no it's not always 0x400000, e.g. 0x034f0000

          writeInt32 os alignVirt //  Section Alignment Always 0x2000 (see Section 23.1).
          writeInt32 os alignPhys // File Alignment Either 0x200 or 0x1000.
       // 000000c0
          writeInt32AsUInt16 os 0x04 //  OS Major Always 4 (see Section 23.1).
          writeInt32AsUInt16 os 0x00 // OS Minor Always 0 (see Section 23.1).
          writeInt32AsUInt16 os 0x00 // User Major Always 0 (see Section 23.1).
          writeInt32AsUInt16 os 0x00 // User Minor Always 0 (see Section 23.1).
          let major, minor = modul.SubsystemVersion
          writeInt32AsUInt16 os major
          writeInt32AsUInt16 os minor
          writeInt32 os 0x00 // Reserved Always 0 (see Section 23.1).
       // 000000d0
          writeInt32 os imageEndAddr // Image Size: Size, in bytes, of image, including all headers and padding
          writeInt32 os headerSectionPhysSize // Header Size Combined size of MS-DOS Header, PE Header, PE Optional Header and padding
          writeInt32 os 0x00 // File Checksum Always 0 (see Section 23.1). QUERY: NOT ALWAYS ZERO
          writeInt32AsUInt16 os modul.SubSystemFlags // SubSystem Subsystem required to run this image.
          // DLL Flags Always 0x400 (no unmanaged windows exception handling - see Section 23.1).
          //  Itanium: see notes at end of file
          //  IMAGE_DLLCHARACTERISTICS_NX_COMPAT: See FSharp 1.0 bug 5019 and http://blogs.msdn.com/ed_maurer/archive/2007/12/14/nxcompat-and-the-c-compiler.aspx
          // Itanium : IMAGE_DLLCHARACTERISTICS_TERMINAL_SERVER_AWARE | IMAGE_DLLCHARACTERISTICS_ NO_SEH | IMAGE_DLL_CHARACTERISTICS_DYNAMIC_BASE | IMAGE_DLLCHARACTERISTICS_NX_COMPAT
          // x86 : IMAGE_DLLCHARACTERISTICS_ NO_SEH | IMAGE_DLL_CHARACTERISTICS_DYNAMIC_BASE | IMAGE_DLLCHARACTERISTICS_NX_COMPAT
          // x64 : IMAGE_DLLCHARACTERISTICS_ NO_SEH | IMAGE_DLL_CHARACTERISTICS_DYNAMIC_BASE | IMAGE_DLLCHARACTERISTICS_NX_COMPAT
          let dllCharacteristics =
            let flags =
                if modul.Is64Bit then (if isItanium then 0x8540 else 0x540)
                else 0x540
            if modul.UseHighEntropyVA then flags ||| 0x20 // IMAGE_DLLCHARACTERISTICS_HIGH_ENTROPY_VA
            else flags
          writeInt32AsUInt16 os dllCharacteristics
       // 000000e0
          // Note that the defaults differ between x86 and x64
          if modul.Is64Bit then
            let size = defaultArg modul.StackReserveSize 0x400000 |> int64
            writeInt64 os size // Stack Reserve Size Always 0x400000 (4Mb) (see Section 23.1).
            writeInt64 os 0x4000L // Stack Commit Size Always 0x4000 (16Kb) (see Section 23.1).
            writeInt64 os 0x100000L // Heap Reserve Size Always 0x100000 (1Mb) (see Section 23.1).
            writeInt64 os 0x2000L // Heap Commit Size Always 0x800 (8Kb) (see Section 23.1).
          else
            let size = defaultArg modul.StackReserveSize 0x100000
            writeInt32 os size // Stack Reserve Size Always 0x100000 (1Mb) (see Section 23.1).
            writeInt32 os 0x1000 // Stack Commit Size Always 0x1000 (4Kb) (see Section 23.1).
            writeInt32 os 0x100000 // Heap Reserve Size Always 0x100000 (1Mb) (see Section 23.1).
            writeInt32 os 0x1000 // Heap Commit Size Always 0x1000 (4Kb) (see Section 23.1).
       // 000000f0 - x86 location, moving on, for x64, add 0x10
          writeInt32 os 0x00 // Loader Flags Always 0 (see Section 23.1)
          writeInt32 os 0x10 // Number of Data Directories: Always 0x10 (see Section 23.1).
          writeInt32 os 0x00
          writeInt32 os 0x00 // Export Table Always 0 (see Section 23.1).
       // 00000100
          writeDirectory os importTableChunk // Import Table RVA of Import Table, (see clause 24.3.1). e.g. 0000b530
          // Native Resource Table: ECMA says Always 0 (see Section 23.1), but mscorlib and other files with resources bound into executable do not.
          writeDirectory os nativeResourcesChunk

       // 00000110
          writeInt32 os 0x00 // Exception Table Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Exception Table Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Certificate Table Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Certificate Table Always 0 (see Section 23.1).
       // 00000120
          writeDirectory os baseRelocTableChunk
          writeDirectory os debugDirectoryChunk // Debug Directory
       // 00000130
          writeInt32 os 0x00 //  Copyright Always 0 (see Section 23.1).
          writeInt32 os 0x00 //  Copyright Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Global Ptr Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Global Ptr Always 0 (see Section 23.1).
       // 00000140
          writeInt32 os 0x00 // Load Config Table Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Load Config Table Always 0 (see Section 23.1).
          writeInt32 os 0x00 // TLS Table Always 0 (see Section 23.1).
          writeInt32 os 0x00 // TLS Table Always 0 (see Section 23.1).
       // 00000150
          writeInt32 os 0x00 // Bound Import Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Bound Import Always 0 (see Section 23.1).
          writeDirectory os importAddrTableChunk // Import Addr Table, (see clause 24.3.1). e.g. 0x00002000
       // 00000160
          writeInt32 os 0x00 // Delay Import Descriptor Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Delay Import Descriptor Always 0 (see Section 23.1).
          writeDirectory os cliHeaderChunk
       // 00000170
          writeInt32 os 0x00 // Reserved Always 0 (see Section 23.1).
          writeInt32 os 0x00 // Reserved Always 0 (see Section 23.1).

          if hasMvidSection then
              write (Some mvidSectionHeaderChunk.addr) os "mvid section header" [| |]
              writeBytes os [| 0x2euy; 0x6Duy; 0x76uy; 0x69uy; 0x64uy; 0x00uy; 0x00uy; 0x00uy; |] // ".mvid\000\000\000"
              writeInt32 os mvidSectionSize     // VirtualSize: Total size of the section when loaded into memory in bytes rounded to Section Alignment.
              writeInt32 os mvidSectionAddr     //  VirtualAddress For executable images this is the address of the first byte of the section
              writeInt32 os mvidSectionPhysSize //  SizeOfRawData Size of the initialized data on disk in bytes
              writeInt32 os mvidSectionPhysLoc  // PointerToRawData RVA to section's first page within the PE file.
              writeInt32 os 0x00                // PointerToRelocations RVA of Relocation section.
              writeInt32 os 0x00                // PointerToLineNumbers Always 0 (see Section 23.1).
              writeInt32AsUInt16 os 0x00        // NumberOfRelocations Number of relocations, set to 0 if unused.
              writeInt32AsUInt16 os 0x00        //  NumberOfLinenumbers Always 0 (see Section 23.1).
              writeBytes os [| 0x20uy; 0x00uy; 0x00uy; 0x60uy |] //  Characteristics Flags IMAGE_SCN_CNT_CODE || IMAGE_SCN_MEM_EXECUTE || IMAGE_SCN_MEM_READ

          write (Some textSectionHeaderChunk.addr) os "text section header" [| |]

       // 00000178
          writeBytes os [| 0x2euy; 0x74uy; 0x65uy; 0x78uy; 0x74uy; 0x00uy; 0x00uy; 0x00uy; |] // ".text\000\000\000"
       // 00000180
          writeInt32 os textSectionSize // VirtualSize: Total size of the section when loaded into memory in bytes rounded to Section Alignment.
          writeInt32 os textSectionAddr //  VirtualAddress For executable images this is the address of the first byte of the section
          writeInt32 os textSectionPhysSize //  SizeOfRawData Size of the initialized data on disk in bytes
          writeInt32 os textSectionPhysLoc // PointerToRawData RVA to section's first page within the PE file.
       // 00000190
          writeInt32 os 0x00 // PointerToRelocations RVA of Relocation section.
          writeInt32 os 0x00 // PointerToLineNumbers Always 0 (see Section 23.1).
       // 00000198
          writeInt32AsUInt16 os 0x00// NumberOfRelocations Number of relocations, set to 0 if unused.
          writeInt32AsUInt16 os 0x00  //  NumberOfLinenumbers Always 0 (see Section 23.1).
          writeBytes os [| 0x20uy; 0x00uy; 0x00uy; 0x60uy |] //  Characteristics Flags IMAGE_SCN_CNT_CODE || IMAGE_SCN_MEM_EXECUTE || IMAGE_SCN_MEM_READ

          write (Some dataSectionHeaderChunk.addr) os "data section header" [| |]

       // 000001a0
          writeBytes os [| 0x2euy; 0x72uy; 0x73uy; 0x72uy; 0x63uy; 0x00uy; 0x00uy; 0x00uy; |] // ".rsrc\000\000\000"
    //  writeBytes os [| 0x2e; 0x73; 0x64; 0x61; 0x74; 0x61; 0x00; 0x00; |] // ".sdata\000\000"
          writeInt32 os dataSectionSize // VirtualSize: Total size of the section when loaded into memory in bytes rounded to Section Alignment.
          writeInt32 os dataSectionAddr //  VirtualAddress For executable images this is the address of the first byte of the section.
       // 000001b0
          writeInt32 os dataSectionPhysSize //  SizeOfRawData Size of the initialized data on disk in bytes,
          writeInt32 os dataSectionPhysLoc // PointerToRawData QUERY: Why does ECMA say "RVA" here? Offset to section's first page within the PE file.
       // 000001b8
          writeInt32 os 0x00 // PointerToRelocations RVA of Relocation section.
          writeInt32 os 0x00 // PointerToLineNumbers Always 0 (see Section 23.1).
       // 000001c0
          writeInt32AsUInt16 os 0x00 // NumberOfRelocations Number of relocations, set to 0 if unused.
          writeInt32AsUInt16 os 0x00  //  NumberOfLinenumbers Always 0 (see Section 23.1).
          writeBytes os [| 0x40uy; 0x00uy; 0x00uy; 0x40uy |] //  Characteristics Flags: IMAGE_SCN_MEM_READ | IMAGE_SCN_CNT_INITIALIZED_DATA

          if hasEntryPointStub then
              write (Some relocSectionHeaderChunk.addr) os "reloc section header" [| |]
       // 000001a0
              writeBytes os [| 0x2euy; 0x72uy; 0x65uy; 0x6cuy; 0x6fuy; 0x63uy; 0x00uy; 0x00uy; |] // ".reloc\000\000"
              writeInt32 os relocSectionSize // VirtualSize: Total size of the section when loaded into memory in bytes rounded to Section Alignment.
              writeInt32 os relocSectionAddr //  VirtualAddress For executable images this is the address of the first byte of the section.
       // 000001b0
              writeInt32 os relocSectionPhysSize //  SizeOfRawData Size of the initialized reloc on disk in bytes
              writeInt32 os relocSectionPhysLoc // PointerToRawData QUERY: Why does ECMA say "RVA" here? Offset to section's first page within the PE file.
       // 000001b8
              writeInt32 os 0x00 // PointerToRelocations RVA of Relocation section.
              writeInt32 os 0x00 // PointerToLineNumbers Always 0 (see Section 23.1).
       // 000001c0
              writeInt32AsUInt16 os 0x00 // NumberOfRelocations Number of relocations, set to 0 if unused.
              writeInt32AsUInt16 os 0x00  //  NumberOfLinenumbers Always 0 (see Section 23.1).
              writeBytes os [| 0x40uy; 0x00uy; 0x00uy; 0x42uy |] //  Characteristics Flags: IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ |

          writePadding os "pad to mvid begin" (mvidSectionPhysLoc - headerSize)

          // MVID SECTION
          if hasMvidSection then
              ignore mvidSectionChunk
              writeBytes os (deterministicMvid())
              writePadding os "pad to mvid begin" (textSectionPhysLoc - (mvidSectionPhysLoc + mvidSectionSize))

          // TEXT SECTION: e.g. 0x200
          let textV2P v = v - textSectionAddr + textSectionPhysLoc

          // e.g. 0x0200
          if hasEntryPointStub then
              write (Some (textV2P importAddrTableChunk.addr)) os "import addr table" [| |]
              writeInt32 os importNameHintTableChunk.addr
              writeInt32 os 0x00  // QUERY 4 bytes of zeros not 2 like ECMA 24.3.1 says

          // e.g. 0x0208
          let flags =
            (if modul.IsILOnly then 0x01 else 0x00) |||
            (if modul.Is32Bit then 0x02 else 0x00) |||
            (if modul.Is32BitPreferred then 0x00020003 else 0x00) |||
            (if (match signer with None -> false | Some s -> s.IsFullySigned) then 0x08 else 0x00)

          let headerVersionMajor, headerVersionMinor = headerVersionSupportedByCLRVersion desiredMetadataVersion

          writePadding os "pad to cli header" cliHeaderPadding
          write (Some (textV2P cliHeaderChunk.addr)) os "cli header" [| |]
          writeInt32 os 0x48 // size of header
          writeInt32AsUInt16 os headerVersionMajor // Major part of minimum version of CLR reqd.
          writeInt32AsUInt16 os headerVersionMinor // Minor part of minimum version of CLR reqd. ...
          // e.g. 0x0210
          writeDirectory os metadataChunk
          writeInt32 os flags

          writeInt32 os entryPointToken
          write None os "rest of cli header" [| |]

          // e.g. 0x0220
          writeDirectory os resourcesChunk
          writeDirectory os strongnameChunk
          // e.g. 0x0230
          writeInt32 os 0x00 // code manager table, always 0
          writeInt32 os 0x00 // code manager table, always 0
          writeDirectory os vtfixupsChunk
          // e.g. 0x0240
          writeInt32 os 0x00  // export addr table jumps, always 0
          writeInt32 os 0x00  // export addr table jumps, always 0
          writeInt32 os 0x00  // managed native header, always 0
          writeInt32 os 0x00  // managed native header, always 0

          writeBytes os code
          write None os "code padding" codePadding

          writeBytes os metadata

          // write 0x80 bytes of empty space for encrypted SHA1 hash, written by SN.EXE or call to signing API
          if signer <> None then
            write (Some (textV2P strongnameChunk.addr)) os "strongname" (Array.create strongnameChunk.size 0x0uy)

          write (Some (textV2P resourcesChunk.addr)) os "raw resources" [| |]
          writeBytes os resources
          write (Some (textV2P rawdataChunk.addr)) os "raw data" [| |]
          writeBytes os data

          if hasEntryPointStub then
              writePadding os "start of import table" importTableChunkPrePadding

              // vtfixups would go here
              write (Some (textV2P importTableChunk.addr)) os "import table" [| |]

              writeInt32 os importLookupTableChunk.addr
              writeInt32 os 0x00
              writeInt32 os 0x00
              writeInt32 os mscoreeStringChunk.addr
              writeInt32 os importAddrTableChunk.addr
              writeInt32 os 0x00
              writeInt32 os 0x00
              writeInt32 os 0x00
              writeInt32 os 0x00
              writeInt32 os 0x00

              write (Some (textV2P importLookupTableChunk.addr)) os "import lookup table" [| |]
              writeInt32 os importNameHintTableChunk.addr
              writeInt32 os 0x00
              writeInt32 os 0x00
              writeInt32 os 0x00
              writeInt32 os 0x00


              write (Some (textV2P importNameHintTableChunk.addr)) os "import name hint table" [| |]
              // Two zero bytes of hint, then Case sensitive, null-terminated ASCII string containing name to import.
              // Shall _CorExeMain a .exe file _CorDllMain for a .dll file.
              if isDll then
                  writeBytes os [| 0x00uy; 0x00uy; 0x5fuy; 0x43uy ; 0x6fuy; 0x72uy; 0x44uy; 0x6cuy; 0x6cuy; 0x4duy; 0x61uy; 0x69uy; 0x6euy; 0x00uy |]
              else
                  writeBytes os [| 0x00uy; 0x00uy; 0x5fuy; 0x43uy; 0x6fuy; 0x72uy; 0x45uy; 0x78uy; 0x65uy; 0x4duy; 0x61uy; 0x69uy; 0x6euy; 0x00uy |]

              write (Some (textV2P mscoreeStringChunk.addr)) os "mscoree string"
                [| 0x6duy; 0x73uy; 0x63uy; 0x6fuy ; 0x72uy; 0x65uy ; 0x65uy; 0x2euy ; 0x64uy; 0x6cuy ; 0x6cuy; 0x00uy ; |]

              writePadding os "end of import tab" importTableChunkPadding

              writePadding os "head of entrypoint" 0x03
              let ep = (imageBaseReal + textSectionAddr)
              write (Some (textV2P entrypointCodeChunk.addr)) os " entrypoint code"
                     [| 0xFFuy; 0x25uy; (* x86 Instructions for entry *) b0 ep; b1 ep; b2 ep; b3 ep |]
              if isItanium then
                  write (Some (textV2P globalpointerCodeChunk.addr)) os " itanium global pointer"
                       [| 0x0uy; 0x0uy; 0x0uy; 0x0uy; 0x0uy; 0x0uy; 0x0uy; 0x0uy |]

          if options.pdbfile.IsSome then
              write (Some (textV2P debugDirectoryChunk.addr)) os "debug directory" (Array.create debugDirectoryChunk.size 0x0uy)
              write (Some (textV2P debugDataChunk.addr)) os "debug data" (Array.create debugDataChunk.size 0x0uy)
              write (Some (textV2P debugChecksumPdbChunk.addr)) os "debug checksum" (Array.create debugChecksumPdbChunk.size 0x0uy)

          if options.embeddedPDB then
              write (Some (textV2P debugEmbeddedPdbChunk.addr)) os "debug data" (Array.create debugEmbeddedPdbChunk.size 0x0uy)

          if options.deterministic then
              write (Some (textV2P debugDeterministicPdbChunk.addr)) os "debug deterministic" Array.empty

          writePadding os "end of .text" (dataSectionPhysLoc - textSectionPhysLoc - textSectionSize)

          // DATA SECTION
          match nativeResources with
          | [||] -> ()
          | resources ->
                write (Some (dataSectionVirtToPhys nativeResourcesChunk.addr)) os "raw native resources" [| |]
                writeBytes os resources

          if dummydatap.size <> 0x0 then
              write (Some (dataSectionVirtToPhys dummydatap.addr)) os "dummy data" [| 0x0uy |]

          writePadding os "end of .rsrc" (relocSectionPhysLoc - dataSectionPhysLoc - dataSectionSize)

          // RELOC SECTION
          if hasEntryPointStub then
              // See ECMA 24.3.2
              let relocV2P v = v - relocSectionAddr + relocSectionPhysLoc

              let entrypointFixupAddr = entrypointCodeChunk.addr + 0x02
              let entrypointFixupBlock = (entrypointFixupAddr / 4096) * 4096
              let entrypointFixupOffset = entrypointFixupAddr - entrypointFixupBlock
              let reloc = (if isItaniumOrAMD then 0xA000 (* IMAGE_REL_BASED_DIR64 *) else 0x3000 (* IMAGE_REL_BASED_HIGHLOW *)) ||| entrypointFixupOffset
              // For the itanium, you need to set a relocation entry for the global pointer
              let reloc2 =
                  if not isItanium then
                      0x0
                  else
                      0xA000 ||| (globalpointerCodeChunk.addr - ((globalpointerCodeChunk.addr / 4096) * 4096))

              write (Some (relocV2P baseRelocTableChunk.addr)) os "base reloc table"
                  [| b0 entrypointFixupBlock; b1 entrypointFixupBlock; b2 entrypointFixupBlock; b3 entrypointFixupBlock
                     0x0cuy; 0x00uy; 0x00uy; 0x00uy
                     b0 reloc; b1 reloc
                     b0 reloc2; b1 reloc2; |]
          writePadding os "end of .reloc" (imageEndSectionPhysLoc - relocSectionPhysLoc - relocSectionSize)

          pdbData, pdbInfoOpt, debugDirectoryChunk, debugDataChunk, debugChecksumPdbChunk, debugEmbeddedPdbChunk, debugDeterministicPdbChunk, textV2P, mappings

    reportTime "Writing Image"
    pdbData, pdbInfoOpt, debugDirectoryChunk, debugDataChunk, debugChecksumPdbChunk, debugEmbeddedPdbChunk, debugDeterministicPdbChunk, textV2P, mappings

let writeBinaryFiles (options: options, modul, normalizeAssemblyRefs) =

    let stream =
        try
            // Ensure the output directory exists otherwise it will fail
            let dir = FileSystem.GetDirectoryNameShim options.outfile
            if not (FileSystem.DirectoryExistsShim dir) then FileSystem.DirectoryCreateShim dir |> ignore
            FileSystem.OpenFileForWriteShim(options.outfile, FileMode.Create, FileAccess.Write, FileShare.Read)
        with _ ->
            failwith ("Could not open file for writing (binary mode): " + options.outfile)

    let pdbData, pdbInfoOpt, debugDirectoryChunk, debugDataChunk, debugChecksumPdbChunk, debugEmbeddedPdbChunk, debugDeterministicPdbChunk, textV2P, mappings =
        try
            try
                writeBinaryAux(stream, options, modul, normalizeAssemblyRefs)
            finally
                stream.Close()

        with _ ->
            try FileSystem.FileDeleteShim options.outfile with | _ -> ()
            reraise()

    let reopenOutput () =
        FileSystem.OpenFileForWriteShim(options.outfile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)

    writePdb (options.dumpDebugInfo,      
        options.embeddedPDB,
        options.pdbfile,
        options.outfile,
        reopenOutput,
        false,
        options.signer,
        options.deterministic,
        options.pathMap,
        pdbData,
        pdbInfoOpt,
        debugDirectoryChunk,
        debugDataChunk,
        debugChecksumPdbChunk,
        debugEmbeddedPdbChunk,
        debugDeterministicPdbChunk,
        textV2P) |> ignore

    mappings

let writeBinaryInMemory (options: options, modul, normalizeAssemblyRefs) =

    let stream = new MemoryStream()
    let options = { options with referenceAssemblyOnly = false; referenceAssemblyAttribOpt = None; referenceAssemblySignatureHash = None }
    let pdbData, pdbInfoOpt, debugDirectoryChunk, debugDataChunk, debugChecksumPdbChunk, debugEmbeddedPdbChunk, debugDeterministicPdbChunk, textV2P, _mappings =
        writeBinaryAux(stream, options, modul, normalizeAssemblyRefs)

    let reopenOutput () =
        stream.Seek(0, SeekOrigin.Begin) |> ignore
        stream

    let pdbBytes =
        writePdb (options.dumpDebugInfo,         
            options.embeddedPDB,
            options.pdbfile,
            options.outfile,
            reopenOutput,
            true,
            options.signer,
            options.deterministic,
            options.pathMap,
            pdbData, pdbInfoOpt,
            debugDirectoryChunk,
            debugDataChunk,
            debugChecksumPdbChunk,
            debugEmbeddedPdbChunk,
            debugDeterministicPdbChunk,
            textV2P)

    stream.Close()

    stream.ToArray(), pdbBytes


let WriteILBinaryFile (options: options, inputModule, normalizeAssemblyRefs) =
    writeBinaryFiles (options, inputModule, normalizeAssemblyRefs)
    |> ignore

let WriteILBinaryInMemory (options: options, inputModule: ILModuleDef, normalizeAssemblyRefs) =
    writeBinaryInMemory (options, inputModule, normalizeAssemblyRefs)
