﻿/* 
MIT License

Copyright (c) 2016 Florian Cäsar, Michael Plainer

For full license see LICENSE in the root directory of this project. 
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Sigma.Core.Data.Extractors;
using Sigma.Core.Data.Sources;
using log4net;
using Sigma.Core.Math;
using System.IO;
using Sigma.Core.Utils;

namespace Sigma.Core.Data.Readers
{
	/// <summary>
	/// A byte record reader, which reads sources bytewise.
	/// </summary>
	public class ByteRecordReader : IRecordReader
	{
		private ILog logger = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

		public IDataSetSource Source { get; private set; }

		private int headerBytes;
		private int recordSizeBytes;
		private bool prepared;
		private bool skippedHeaderBytes;

		/// <summary>
		/// Create a byte record reader with a certain header size and per record size.
		/// </summary>
		/// <param name="source">The source which should read.</param>
		/// <param name="headerLengthBytes">The header length in bytes (which will be skipped in this implementation).</param>
		/// <param name="recordSizeBytes">The per record size in bytes.</param>
		public ByteRecordReader(IDataSetSource source, int headerLengthBytes, int recordSizeBytes)
		{
			if (source == null)
			{
				throw new ArgumentNullException("Source cannot be null.");
			}

			if (headerLengthBytes < 0)
			{
				throw new ArgumentException($"Header bytes must be >= 0, but header bytes was {headerLengthBytes}.");
			}

			if (recordSizeBytes < 0)
			{
				throw new ArgumentException($"Record size bytes must be >= 0, but record size bytes was {recordSizeBytes}.");
			}

			this.Source = source;
			this.headerBytes = headerLengthBytes;
			this.recordSizeBytes = recordSizeBytes;
		}

		public void Prepare()
		{
			if (prepared)
			{
				return;
			}

			Source.Prepare();

			prepared = true;
		}

		public object Read(int numberOfRecords)
		{
			if (!prepared)
			{
				throw new InvalidOperationException("Cannot read from source before preparing this reader (missing Prepare() call?).");
			}

			Stream stream = Source.Retrieve();

			if (!skippedHeaderBytes)
			{
				int read = stream.Read(new byte[headerBytes], 0, headerBytes);

				skippedHeaderBytes = true;

				if (read != headerBytes)
				{
					return null;
				}
			}

			List<byte[]> records = new List<byte[]>();

			int numberOfRecordsRead = 0;
			while (numberOfRecordsRead < numberOfRecords)
			{
				byte[] buffer = new byte[recordSizeBytes];

				int readBytes = stream.Read(buffer, 0, recordSizeBytes);

				if (readBytes != recordSizeBytes)
				{
					break;
				}

				records.Add(buffer);

				numberOfRecordsRead++;
			}

			return records.ToArray();
		}

		public ByteRecordExtractor Extractor(params object[] args)
		{
			if (args.Length == 0)
			{
				throw new ArgumentException("Extractor parameters cannot be empty.");
			}

			Dictionary<string, long[][]> indexMappings = new Dictionary<string, long[][]>();

			string currentNamedSection = null;
			IList<long[]> currentParams = null;
			int paramIndex = 0;

			foreach (object param in args)
			{
				if (param is string)
				{
					string previousSection = currentNamedSection;
					currentNamedSection = (string) param;

					if (indexMappings.ContainsKey(currentNamedSection))
					{
						throw new ArgumentException($"Named sections can only be used once, but section {currentNamedSection} (argument {paramIndex}) was already used.");
					}

					if (previousSection != null)
					{
						AddNamedIndexParameters(indexMappings, previousSection, currentParams, paramIndex);
					}

					currentParams = new List<long[]>();
				}
				else if (param is long[])
				{
					if (currentNamedSection == null)
					{
						throw new ArgumentException("Cannot assign parameters without naming a section.");
					}

					long[] indexRange = (long[]) param;

					for (int i = 0; i < indexRange.Length; i++)
					{
						if (indexRange[i] < 0)
						{
							throw new ArgumentException($"All parameters in index range have to be >= 0, but element at index {i} of parameter {paramIndex} was {indexRange[i]}.");
						}
					}

					currentParams.Add(indexRange);
				}
				else
				{
					throw new ArgumentException("All parameters must be either of type string or long[].");
				}

				paramIndex++;
			}

			if (currentNamedSection != null && !indexMappings.ContainsKey(currentNamedSection))
			{
				AddNamedIndexParameters(indexMappings, currentNamedSection, currentParams, paramIndex);
			}

			return (ByteRecordExtractor) Extractor(indexMappings);
		}

		private void AddNamedIndexParameters(Dictionary<string, long[][]> indexMappings, string currentNamedSection, IList<long[]> currentParams, int paramIndex)
		{
			if (currentParams.Count < 1)
			{
				throw new ArgumentException($"There must be >= 1 index regions, but in section {currentNamedSection} at parameter index {paramIndex} there were {currentParams.Count}.");
			}

			indexMappings.Add(currentNamedSection, currentParams.ToArray());
		}

		public IRecordExtractor Extractor(Dictionary<string, long[][]> indexMappings)
		{
			return Extractor(new ByteRecordExtractor(indexMappings));
		}

		public IRecordExtractor Extractor(IRecordExtractor extractor)
		{
			extractor.Reader = this;

			return extractor;
		}

		public void Dispose()
		{
		}
	}
}
