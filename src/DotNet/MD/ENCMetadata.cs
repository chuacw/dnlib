// dnlib: See LICENSE.txt for more info

using System;
using System.Collections.Generic;
using System.Diagnostics;
using dnlib.IO;
using dnlib.PE;
using dnlib.Threading;

namespace dnlib.DotNet.MD {
	/// <summary>
	/// Used when a #- stream is present in the metadata
	/// </summary>
	sealed class ENCMetadata : MetadataBase {
		static readonly UTF8String DeletedName = "_Deleted";
		bool hasMethodPtr, hasFieldPtr, hasParamPtr, hasEventPtr, hasPropertyPtr;
		bool hasDeletedRows;
		readonly CLRRuntimeReaderKind runtime;
		readonly Dictionary<Table, SortedTable> sortedTables = new Dictionary<Table, SortedTable>();
#if THREAD_SAFE
		readonly Lock theLock = Lock.Create();
#endif

		/// <inheritdoc/>
		public override bool IsCompressed => false;

		/// <inheritdoc/>
		public ENCMetadata(IPEImage peImage, ImageCor20Header cor20Header, MetadataHeader mdHeader, CLRRuntimeReaderKind runtime)
			: base(peImage, cor20Header, mdHeader) {
			this.runtime = runtime;
		}

		/// <inheritdoc/>
		internal ENCMetadata(MetadataHeader mdHeader, bool isStandalonePortablePdb, CLRRuntimeReaderKind runtime)
			: base(mdHeader, isStandalonePortablePdb) {
			this.runtime = runtime;
		}

		/// <inheritdoc/>
		protected override void InitializeInternal(DataReaderFactory mdReaderFactory, uint metadataBaseOffset) {
			DotNetStream dns = null;
			try {
				if (runtime == CLRRuntimeReaderKind.Mono) {
					var newAllStreams = new List<DotNetStream>(allStreams);
					for (int i = mdHeader.StreamHeaders.Count - 1; i >= 0; i--) {
						var sh = mdHeader.StreamHeaders[i];
						switch (sh.Name) {
						case "#Strings":
							if (stringsStream is null) {
								stringsStream = new StringsStream(mdReaderFactory, metadataBaseOffset, sh);
								newAllStreams.Add(stringsStream);
								continue;
							}
							break;

						case "#US":
							if (usStream is null) {
								usStream = new USStream(mdReaderFactory, metadataBaseOffset, sh);
								newAllStreams.Add(usStream);
								continue;
							}
							break;

						case "#Blob":
							if (blobStream is null) {
								blobStream = new BlobStream(mdReaderFactory, metadataBaseOffset, sh);
								newAllStreams.Add(blobStream);
								continue;
							}
							break;

						case "#GUID":
							if (guidStream is null) {
								guidStream = new GuidStream(mdReaderFactory, metadataBaseOffset, sh);
								newAllStreams.Add(guidStream);
								continue;
							}
							break;

						case "#~":
						case "#-":
							if (tablesStream is null) {
								tablesStream = new TablesStream(mdReaderFactory, metadataBaseOffset, sh, runtime);
								newAllStreams.Add(tablesStream);
								continue;
							}
							break;

						case "#Pdb":
							if (isStandalonePortablePdb && pdbStream is null) {
								pdbStream = new PdbStream(mdReaderFactory, metadataBaseOffset, sh);
								newAllStreams.Add(pdbStream);
								continue;
							}
							break;
						}
						dns = new CustomDotNetStream(mdReaderFactory, metadataBaseOffset, sh);
						newAllStreams.Add(dns);
						dns = null;
					}
					newAllStreams.Reverse();
					allStreams = newAllStreams;
				}
				else {
					Debug.Assert(runtime == CLRRuntimeReaderKind.CLR);
					foreach (var sh in mdHeader.StreamHeaders) {
						switch (sh.Name.ToUpperInvariant()) {
						case "#STRINGS":
							if (stringsStream is null) {
								stringsStream = new StringsStream(mdReaderFactory, metadataBaseOffset, sh);
								allStreams.Add(stringsStream);
								continue;
							}
							break;

						case "#US":
							if (usStream is null) {
								usStream = new USStream(mdReaderFactory, metadataBaseOffset, sh);
								allStreams.Add(usStream);
								continue;
							}
							break;

						case "#BLOB":
							if (blobStream is null) {
								blobStream = new BlobStream(mdReaderFactory, metadataBaseOffset, sh);
								allStreams.Add(blobStream);
								continue;
							}
							break;

						case "#GUID":
							if (guidStream is null) {
								guidStream = new GuidStream(mdReaderFactory, metadataBaseOffset, sh);
								allStreams.Add(guidStream);
								continue;
							}
							break;

						case "#~":  // Only if #Schema is used
						case "#-":
							if (tablesStream is null) {
								tablesStream = new TablesStream(mdReaderFactory, metadataBaseOffset, sh, runtime);
								allStreams.Add(tablesStream);
								continue;
							}
							break;

						case "#PDB":
							// Case sensitive comparison since it's a stream that's not read by the CLR,
							// only by other libraries eg. System.Reflection.Metadata.
							if (isStandalonePortablePdb && pdbStream is null && sh.Name == "#Pdb") {
								pdbStream = new PdbStream(mdReaderFactory, metadataBaseOffset, sh);
								allStreams.Add(pdbStream);
								continue;
							}
							break;
						}
						dns = new CustomDotNetStream(mdReaderFactory, metadataBaseOffset, sh);
						allStreams.Add(dns);
						dns = null;
					}
				}
			}
			finally {
				dns?.Dispose();
			}

			if (tablesStream is null)
				throw new BadImageFormatException("Missing MD stream");

			if (pdbStream is not null)
				tablesStream.Initialize(pdbStream.TypeSystemTableRows);
			else
				tablesStream.Initialize(null);

			// The pointer tables are used iff row count != 0
			hasFieldPtr = !tablesStream.FieldPtrTable.IsEmpty;
			hasMethodPtr = !tablesStream.MethodPtrTable.IsEmpty;
			hasParamPtr = !tablesStream.ParamPtrTable.IsEmpty;
			hasEventPtr = !tablesStream.EventPtrTable.IsEmpty;
			hasPropertyPtr = !tablesStream.PropertyPtrTable.IsEmpty;
			hasDeletedRows = tablesStream.HasDelete;
		}

		/// <inheritdoc/>
		public override RidList GetTypeDefRidList() {
			if (!hasDeletedRows)
				return base.GetTypeDefRidList();
			uint rows = tablesStream.TypeDefTable.Rows;
			var list = new List<uint>((int)rows);
			for (uint rid = 1; rid <= rows; rid++) {
				if (!tablesStream.TryReadTypeDefRow(rid, out var row))
					continue;	// Should never happen since rid is valid

				// RTSpecialName is ignored by the CLR. It's only the name that indicates
				// whether it's been deleted.
				if (stringsStream.ReadNoNull(row.Name).StartsWith(DeletedName))
					continue;	// ignore this deleted row
				list.Add(rid);
			}
			return RidList.Create(list);
		}

		/// <inheritdoc/>
		public override RidList GetExportedTypeRidList() {
			if (!hasDeletedRows)
				return base.GetExportedTypeRidList();
			uint rows = tablesStream.ExportedTypeTable.Rows;
			var list = new List<uint>((int)rows);
			for (uint rid = 1; rid <= rows; rid++) {
				if (!tablesStream.TryReadExportedTypeRow(rid, out var row))
					continue;	// Should never happen since rid is valid

				// RTSpecialName is ignored by the CLR. It's only the name that indicates
				// whether it's been deleted.
				if (stringsStream.ReadNoNull(row.TypeName).StartsWith(DeletedName))
					continue;	// ignore this deleted row
				list.Add(rid);
			}
			return RidList.Create(list);
		}

		/// <summary>
		/// Converts a logical <c>Field</c> rid to a physical <c>Field</c> rid
		/// </summary>
		/// <param name="listRid">A valid rid</param>
		/// <returns>Converted rid or any invalid rid value if <paramref name="listRid"/> is invalid</returns>
		uint ToFieldRid(uint listRid) {
			if (!hasFieldPtr)
				return listRid;
			return tablesStream.TryReadColumn24(tablesStream.FieldPtrTable, listRid, 0, out uint listValue) ? listValue : 0;
		}

		/// <summary>
		/// Converts a logical <c>Method</c> rid to a physical <c>Method</c> rid
		/// </summary>
		/// <param name="listRid">A valid rid</param>
		/// <returns>Converted rid or any invalid rid value if <paramref name="listRid"/> is invalid</returns>
		uint ToMethodRid(uint listRid) {
			if (!hasMethodPtr)
				return listRid;
			return tablesStream.TryReadColumn24(tablesStream.MethodPtrTable, listRid, 0, out uint listValue) ? listValue : 0;
		}

		/// <summary>
		/// Converts a logical <c>Param</c> rid to a physical <c>Param</c> rid
		/// </summary>
		/// <param name="listRid">A valid rid</param>
		/// <returns>Converted rid or any invalid rid value if <paramref name="listRid"/> is invalid</returns>
		uint ToParamRid(uint listRid) {
			if (!hasParamPtr)
				return listRid;
			return tablesStream.TryReadColumn24(tablesStream.ParamPtrTable, listRid, 0, out uint listValue) ? listValue : 0;
		}

		/// <summary>
		/// Converts a logical <c>Event</c> rid to a physical <c>Event</c> rid
		/// </summary>
		/// <param name="listRid">A valid rid</param>
		/// <returns>Converted rid or any invalid rid value if <paramref name="listRid"/> is invalid</returns>
		uint ToEventRid(uint listRid) {
			if (!hasEventPtr)
				return listRid;
			return tablesStream.TryReadColumn24(tablesStream.EventPtrTable, listRid, 0, out uint listValue) ? listValue : 0;
		}

		/// <summary>
		/// Converts a logical <c>Property</c> rid to a physical <c>Property</c> rid
		/// </summary>
		/// <param name="listRid">A valid rid</param>
		/// <returns>Converted rid or any invalid rid value if <paramref name="listRid"/> is invalid</returns>
		uint ToPropertyRid(uint listRid) {
			if (!hasPropertyPtr)
				return listRid;
			return tablesStream.TryReadColumn24(tablesStream.PropertyPtrTable, listRid, 0, out uint listValue) ? listValue : 0;
		}

		/// <inheritdoc/>
		public override RidList GetFieldRidList(uint typeDefRid) {
			var list = GetRidList(tablesStream.TypeDefTable, typeDefRid, 4, tablesStream.FieldTable);
			if (list.Count == 0 || (!hasFieldPtr && !hasDeletedRows))
				return list;

			var destTable = tablesStream.FieldTable;
			var newList = new List<uint>(list.Count);
			for (int i = 0; i < list.Count; i++) {
				var rid = ToFieldRid(list[i]);
				if (destTable.IsInvalidRID(rid))
					continue;
				if (hasDeletedRows) {
					// It's a deleted row if RTSpecialName is set and name is "_Deleted"
					if (!tablesStream.TryReadFieldRow(rid, out var row))
						continue;	// Should never happen since rid is valid
					if ((row.Flags & (uint)FieldAttributes.RTSpecialName) != 0) {
						if (stringsStream.ReadNoNull(row.Name).StartsWith(DeletedName))
							continue;	// ignore this deleted row
					}
				}
				// It's a valid non-deleted rid so add it
				newList.Add(rid);
			}
			return RidList.Create(newList);
		}

		/// <inheritdoc/>
		public override RidList GetMethodRidList(uint typeDefRid) {
			var list = GetRidList(tablesStream.TypeDefTable, typeDefRid, 5, tablesStream.MethodTable);
			if (list.Count == 0 || (!hasMethodPtr && !hasDeletedRows))
				return list;

			var destTable = tablesStream.MethodTable;
			var newList = new List<uint>(list.Count);
			for (int i = 0; i < list.Count; i++) {
				var rid = ToMethodRid(list[i]);
				if (destTable.IsInvalidRID(rid))
					continue;
				if (hasDeletedRows) {
					// It's a deleted row if RTSpecialName is set and name is "_Deleted"
					if (!tablesStream.TryReadMethodRow(rid, out var row))
						continue;	// Should never happen since rid is valid
					if ((row.Flags & (uint)MethodAttributes.RTSpecialName) != 0) {
						if (stringsStream.ReadNoNull(row.Name).StartsWith(DeletedName))
							continue;	// ignore this deleted row
					}
				}
				// It's a valid non-deleted rid so add it
				newList.Add(rid);
			}
			return RidList.Create(newList);
		}

		/// <inheritdoc/>
		public override RidList GetParamRidList(uint methodRid) {
			var list = GetRidList(tablesStream.MethodTable, methodRid, 5, tablesStream.ParamTable);
			if (list.Count == 0 || !hasParamPtr)
				return list;

			var destTable = tablesStream.ParamTable;
			var newList = new List<uint>(list.Count);
			for (int i = 0; i < list.Count; i++) {
				var rid = ToParamRid(list[i]);
				if (destTable.IsInvalidRID(rid))
					continue;
				newList.Add(rid);
			}
			return RidList.Create(newList);
		}

		/// <inheritdoc/>
		public override RidList GetEventRidList(uint eventMapRid) {
			var list = GetRidList(tablesStream.EventMapTable, eventMapRid, 1, tablesStream.EventTable);
			if (list.Count == 0 || (!hasEventPtr && !hasDeletedRows))
				return list;

			var destTable = tablesStream.EventTable;
			var newList = new List<uint>(list.Count);
			for (int i = 0; i < list.Count; i++) {
				var rid = ToEventRid(list[i]);
				if (destTable.IsInvalidRID(rid))
					continue;
				if (hasDeletedRows) {
					// It's a deleted row if RTSpecialName is set and name is "_Deleted"
					if (!tablesStream.TryReadEventRow(rid, out var row))
						continue;	// Should never happen since rid is valid
					if ((row.EventFlags & (uint)EventAttributes.RTSpecialName) != 0) {
						if (stringsStream.ReadNoNull(row.Name).StartsWith(DeletedName))
							continue;	// ignore this deleted row
					}
				}
				// It's a valid non-deleted rid so add it
				newList.Add(rid);
			}
			return RidList.Create(newList);
		}

		/// <inheritdoc/>
		public override RidList GetPropertyRidList(uint propertyMapRid) {
			var list = GetRidList(tablesStream.PropertyMapTable, propertyMapRid, 1, tablesStream.PropertyTable);
			if (list.Count == 0 || (!hasPropertyPtr && !hasDeletedRows))
				return list;

			var destTable = tablesStream.PropertyTable;
			var newList = new List<uint>(list.Count);
			for (int i = 0; i < list.Count; i++) {
				var rid = ToPropertyRid(list[i]);
				if (destTable.IsInvalidRID(rid))
					continue;
				if (hasDeletedRows) {
					// It's a deleted row if RTSpecialName is set and name is "_Deleted"
					if (!tablesStream.TryReadPropertyRow(rid, out var row))
						continue;	// Should never happen since rid is valid
					if ((row.PropFlags & (uint)PropertyAttributes.RTSpecialName) != 0) {
						if (stringsStream.ReadNoNull(row.Name).StartsWith(DeletedName))
							continue;	// ignore this deleted row
					}
				}
				// It's a valid non-deleted rid so add it
				newList.Add(rid);
			}
			return RidList.Create(newList);
		}

		/// <summary>
		/// Gets a rid list (eg. field list)
		/// </summary>
		/// <param name="tableSource">Source table, eg. <c>TypeDef</c></param>
		/// <param name="tableSourceRid">Row ID in <paramref name="tableSource"/></param>
		/// <param name="colIndex">Column index in <paramref name="tableSource"/>, eg. 4 for <c>TypeDef.FieldList</c></param>
		/// <param name="tableDest">Destination table, eg. <c>Field</c></param>
		/// <returns>A new <see cref="RidList"/> instance</returns>
		RidList GetRidList(MDTable tableSource, uint tableSourceRid, int colIndex, MDTable tableDest) {
			var column = tableSource.TableInfo.Columns[colIndex];
			if (!tablesStream.TryReadColumn24(tableSource, tableSourceRid, column, out uint startRid))
				return RidList.Empty;
			bool hasNext = tablesStream.TryReadColumn24(tableSource, tableSourceRid + 1, column, out uint nextListRid);
			uint lastRid = tableDest.Rows + 1;
			if (startRid == 0 || startRid >= lastRid)
				return RidList.Empty;
			uint endRid = hasNext && nextListRid != 0 ? nextListRid : lastRid;
			if (endRid < startRid)
				endRid = startRid;
			if (endRid > lastRid)
				endRid = lastRid;
			return RidList.Create(startRid, endRid - startRid);
		}

		/// <inheritdoc/>
		protected override uint BinarySearch(MDTable tableSource, int keyColIndex, uint key) {
			var keyColumn = tableSource.TableInfo.Columns[keyColIndex];
			uint ridLo = 1, ridHi = tableSource.Rows;
			while (ridLo <= ridHi) {
				uint rid = (ridLo + ridHi) / 2;
				if (!tablesStream.TryReadColumn24(tableSource, rid, keyColumn, out uint key2))
					break;	// Never happens since rid is valid
				if (key == key2)
					return rid;
				if (key2 > key)
					ridHi = rid - 1;
				else
					ridLo = rid + 1;
			}

			if (tableSource.Table == Table.GenericParam && !tablesStream.IsSorted(tableSource))
				return LinearSearch(tableSource, keyColIndex, key);

			return 0;
		}

		/// <summary>
		/// Linear searches the table (O(n)) for a <c>rid</c> whose key column at index
		/// <paramref name="keyColIndex"/> is equal to <paramref name="key"/>.
		/// </summary>
		/// <param name="tableSource">Table to search</param>
		/// <param name="keyColIndex">Key column index</param>
		/// <param name="key">Key</param>
		/// <returns>The <c>rid</c> of the found row, or 0 if none found</returns>
		uint LinearSearch(MDTable tableSource, int keyColIndex, uint key) {
			if (tableSource is null)
				return 0;
			var keyColumn = tableSource.TableInfo.Columns[keyColIndex];
			for (uint rid = 1; rid <= tableSource.Rows; rid++) {
				if (!tablesStream.TryReadColumn24(tableSource, rid, keyColumn, out uint key2))
					break;	// Never happens since rid is valid
				if (key == key2)
					return rid;
			}
			return 0;
		}

		/// <inheritdoc/>
		protected override RidList FindAllRowsUnsorted(MDTable tableSource, int keyColIndex, uint key) {
			if (tablesStream.IsSorted(tableSource))
				return FindAllRows(tableSource, keyColIndex, key);
			SortedTable sortedTable;
#if THREAD_SAFE
			theLock.EnterWriteLock(); try {
#endif
			if (!sortedTables.TryGetValue(tableSource.Table, out sortedTable))
				sortedTables[tableSource.Table] = sortedTable = new SortedTable(tableSource, keyColIndex);
#if THREAD_SAFE
			} finally { theLock.ExitWriteLock(); }
#endif
			return sortedTable.FindAllRows(key);
		}
	}
}
