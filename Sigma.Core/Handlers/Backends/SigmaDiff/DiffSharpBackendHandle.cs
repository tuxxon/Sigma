﻿/* 
MIT License

Copyright (c) 2016-2017 Florian Cäsar, Michael Plainer

For full license see LICENSE in the root directory of this project. 
*/

using System;
using System.Collections.Generic;
using DiffSharp.Backend;
using static DiffSharp.Util;
using Microsoft.FSharp.Core;
using Sigma.Core.Persistence;
using Sigma.Core.Utils;
using Array = System.Array;

namespace Sigma.Core.Handlers.Backends.SigmaDiff
{
	/// <summary>
	/// A DiffSharp backend handle, as passed to the backend provider and used by Sigma.DiffSharp internally for direct operations on Blas and Lapack backends.
	/// </summary>
	/// <typeparam name="T">The primitive data type processed by this backend handle.</typeparam>
	[Serializable]
	public abstract class DiffSharpBackendHandle<T> : Backend<T>, ISerialisationNotifier
	{
		private IDictionary<int, IList<T[]>> _bufferedSessionArrays;
		private IDictionary<int, IList<T[]>> _currentSessionArrays;
		private readonly ISet<T[]> _limboSessionArrays; // arrays that aren't automatically freed at the end of a session (for reuse), have to be especially marked (e.g. for parameters)

		public bool BufferSessions { get; set; }
		public long BackendTag { get; set; }

		internal DiffSharpBackendHandle(long backendTag)
		{
			BackendTag = backendTag;

			_bufferedSessionArrays = new Dictionary<int, IList<T[]>>();
			_currentSessionArrays = new Dictionary<int, IList<T[]>>();
			_limboSessionArrays = new HashSet<T[]>();
		}

		private void _InternalAddToCurrentSession(T[] array)
		{
			if (!_currentSessionArrays.ContainsKey(array.Length))
			{
				_currentSessionArrays.Add(array.Length, new List<T[]>());
			}

			_currentSessionArrays[array.Length].Add(array);
		}

		private T[] _InternalGetBufferedArray(int length)
		{
			if (!_bufferedSessionArrays.ContainsKey(length) || _bufferedSessionArrays[length].Count == 0)
			{
				return null;
			}

			IList<T[]> buffer = _bufferedSessionArrays[length];

			lock (buffer)
			{
				T[] array = buffer[buffer.Count - 1];

				buffer.RemoveAt(buffer.Count - 1);

				return array;
			}
		}

		internal virtual void ClearSessionBuffers()
		{
			_bufferedSessionArrays.Clear();
			_currentSessionArrays.Clear();
			_limboSessionArrays.Clear();
		}

		internal virtual void TransferSessionBuffers()
		{
			_bufferedSessionArrays.Clear();
			_bufferedSessionArrays.AddAll(_currentSessionArrays);
			_currentSessionArrays.Clear();
		}

		internal void Flush(T[] array)
		{
			if (BufferSessions && _currentSessionArrays.ContainsKey(array.Length) && _currentSessionArrays[array.Length].Remove(array))
			{
				if (!_bufferedSessionArrays.ContainsKey(array.Length))
				{
					_bufferedSessionArrays.Add(array.Length, new List<T[]>());
				}

				_bufferedSessionArrays[array.Length].Add(array);
			}
		}

		internal void MarkLimbo(T[] array)
		{
			if (BufferSessions && _currentSessionArrays.ContainsKey(array.Length) && _currentSessionArrays[array.Length].Remove(array))
			{
				_limboSessionArrays.Add(array);
			}
		}

		internal void FreeLimbo(T[] array)
		{
			if (BufferSessions && _limboSessionArrays.Contains(array))
			{
				_limboSessionArrays.Remove(array);
				_InternalAddToCurrentSession(array);
			}
		}

		/// <inheritdoc />
		public T[] CreateUninitialisedArray(int length)
		{
			T[] array;

			if (!BufferSessions || (array = _InternalGetBufferedArray(length)) == null)
			{
				array = new T[length];
			}

			if (BufferSessions)
			{
				_InternalAddToCurrentSession(array);
			}

			OnUninitialisedArrayCreated(array);
				
			return array;
		}

		/// <inheritdoc />
		public T[] CreateZeroArray(int length)
		{
			return CreateValueArray(length, default(T));
		}

		/// <inheritdoc />
		public T[] CreateValueArray(int length, T initialValue)
		{
			T[] array;
			bool alreadyInitialised = false;
			bool initialIsDefault = initialValue.Equals(default(T));

			if (!BufferSessions || (array = _InternalGetBufferedArray(length)) == null)
			{
				array = new T[length];
				alreadyInitialised = initialIsDefault;
			}

			if (BufferSessions)
			{
				_InternalAddToCurrentSession(array);
			}

			if (!alreadyInitialised)
			{
				if (initialIsDefault)
				{
					Array.Clear(array, 0, array.Length);
				}
				else
				{
					if (array.Length <= 8)
					{
						for (var i = 0; i < array.Length; i++)
						{
							array[i] = initialValue;
						}
					}
					else
					{
						array[0] = initialValue;
						array[1] = initialValue;
						array[2] = initialValue;
						array[3] = initialValue;
						array[4] = initialValue;
						array[5] = initialValue;
						array[6] = initialValue;
						array[7] = initialValue;

						int arrayToFillHalfLength = array.Length / 2;
						int copyLength;

						for (copyLength = 8; copyLength < arrayToFillHalfLength; copyLength <<= 1)
						{
							Array.Copy(array, 0, array, copyLength, copyLength);
						}

						Array.Copy(array, 0, array, copyLength, array.Length - copyLength);
					}
				}
			}

			OnValueArrayCreated(array, initialValue);

			return array;
		}

		/// <summary>
		/// Called when an uninitialised value array is "created" (from cache or allocated).
		/// </summary>
		/// <param name="array">The array.</param>
		protected virtual void OnUninitialisedArrayCreated(T[] array)
		{
		}

		/// <summary>
		/// Called when a value array is "created" (from cache or allocated).
		/// </summary>
		/// <param name="array">The array.</param>
		/// <param name="initialValue">The initial value.</param>
		protected virtual void OnValueArrayCreated(T[] array, T initialValue)
		{
		}

		/// <summary>
		/// Check if a certain array is in any way registered (or buffered) in this backend.
		/// </summary>
		/// <param name="array">The array.</param>
		/// <returns>A boolean indicating whether or not the given array is buffered in this backend.</returns>
		protected bool IsRegistered(T[] array)
		{
			int length = array.Length;

			if (_currentSessionArrays.ContainsKey(length))
			{
				if (_currentSessionArrays[length].Contains(array))
				{
					return true;
				}
			}

			if (_bufferedSessionArrays.ContainsKey(length))
			{
				if (_bufferedSessionArrays[length].Contains(array))
				{
					return true;
				}
			}

			if (_limboSessionArrays.Contains(array))
			{
				return true;
			}

			return false;
		}

		public abstract ISigmaDiffDataBuffer<T> CreateDataBuffer(T[] values);
		public abstract T Mul_Dot_V_V(ISigmaDiffDataBuffer<T> a, ISigmaDiffDataBuffer<T> n);
		public abstract T L1Norm_V(ISigmaDiffDataBuffer<T> value);
		public abstract T L2Norm_V(ISigmaDiffDataBuffer<T> value);
		public abstract T SupNorm_V(ISigmaDiffDataBuffer<T> value);
		public abstract T Sum_V(ISigmaDiffDataBuffer<T> value);
		public abstract T Sum_M(ISigmaDiffDataBuffer<T> value);
		public abstract int MaxIndex_V(ISigmaDiffDataBuffer<T> value);
		public abstract int MinIndex_V(ISigmaDiffDataBuffer<T> value);
		public abstract ISigmaDiffDataBuffer<T> Add_V_V(ISigmaDiffDataBuffer<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract ISigmaDiffDataBuffer<T> Add_V_V_InPlace(ISigmaDiffDataBuffer<T> obj0, int obj1, ISigmaDiffDataBuffer<T> obj2, int obj3, int obj4);
		public abstract ISigmaDiffDataBuffer<T> Add_S_V(T a, ISigmaDiffDataBuffer<T> b);
		public abstract ISigmaDiffDataBuffer<T> Sub_V_V(ISigmaDiffDataBuffer<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract ISigmaDiffDataBuffer<T> Sub_S_V(T a, ISigmaDiffDataBuffer<T> b);
		public abstract ISigmaDiffDataBuffer<T> Sub_V_S(ISigmaDiffDataBuffer<T> a, T b);
		public abstract ISigmaDiffDataBuffer<T> Mul_S_V(T a, ISigmaDiffDataBuffer<T> b);
		public abstract ISigmaDiffDataBuffer<T> Mul_M_V(ShapedDataBufferView<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract ISigmaDiffDataBuffer<T> Mul_M_V_Add_V(ShapedDataBufferView<T> a, ISigmaDiffDataBuffer<T> b, ISigmaDiffDataBuffer<T> obj2);
		public abstract ISigmaDiffDataBuffer<T> Mul_V_M(ISigmaDiffDataBuffer<T> a, ShapedDataBufferView<T> b);
		public abstract FSharpOption<ISigmaDiffDataBuffer<T>> Solve_M_V(ShapedDataBufferView<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract FSharpOption<ISigmaDiffDataBuffer<T>> SolveSymmetric_M_V(ShapedDataBufferView<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract ISigmaDiffDataBuffer<T> Diagonal_M(ShapedDataBufferView<T> a);
		public abstract ISigmaDiffDataBuffer<T> ReshapeCopy_MRows_V(ShapedDataBufferView<T> value);
		public abstract ShapedDataBufferView<T> Mul_Out_V_V(ISigmaDiffDataBuffer<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract ShapedDataBufferView<T> Add_M_M(ShapedDataBufferView<T> a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Add_M_M_InPlace(ShapedDataBufferView<T> a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Add_S_M(T a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Add_V_MCols(ISigmaDiffDataBuffer<T> a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Sub_M_M(ShapedDataBufferView<T> a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Sub_M_S(ShapedDataBufferView<T> a, T b);
		public abstract ShapedDataBufferView<T> Sub_S_M(T a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Mul_M_M(ShapedDataBufferView<T> a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Mul_S_M(T a, ShapedDataBufferView<T> b);
		public abstract ShapedDataBufferView<T> Mul_M_M_Add_V_MCols(ShapedDataBufferView<T> a, ShapedDataBufferView<T> b, ISigmaDiffDataBuffer<T> obj2);
		public abstract ISigmaDiffDataBuffer<T> Add_M_Colwise_V_InPlace(ShapedDataBufferView<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract ShapedDataBufferView<T> Mul_Had_M_M(ShapedDataBufferView<T> a, ShapedDataBufferView<T> b);
		public abstract FSharpOption<ShapedDataBufferView<T>> Inverse_M(ShapedDataBufferView<T> a);
		public abstract FSharpOption<T> Det_M(ShapedDataBufferView<T> a);
		public abstract ShapedDataBufferView<T> Transpose_M(ShapedDataBufferView<T> a);
		public abstract ShapedDataBufferView<T> Permute_M(ShapedDataBufferView<T> array, int[] rearrangedDimensions);
		public abstract ShapedDataBufferView<T> Reshape_M(ShapedDataBufferView<T> array, long[] newShape);
		public abstract ShapedDataBufferView<T> ReshapeCopy_V_MRows(int rows, ISigmaDiffDataBuffer<T> value);
		public abstract ShapedDataBufferView<T> RepeatReshapeCopy_V_MRows(int rows, ISigmaDiffDataBuffer<T> row);
		public abstract ShapedDataBufferView<T> RepeatReshapeCopy_V_MCols(int cols, ISigmaDiffDataBuffer<T> value);
		public abstract ShapedDataBufferView<T> CustomOp_DM_Forward(ShapedDataBufferView<T> value, object customInfo);
		public abstract ShapedDataBufferView<T> CustomOp_DM_Backward(ShapedDataBufferView<T> origin, ShapedDataBufferView<T> adjoint, ShapedDataBufferView<T> primal, object customInfo);

		public abstract ISigmaDiffDataBuffer<T> Map_F_V(MapOp mapOp, FSharpFunc<T, T> function, ISigmaDiffDataBuffer<T> value);
		public abstract ISigmaDiffDataBuffer<T> Map_F_S_V(T other, MapOp mapOp, FSharpFunc<T, T> function, ISigmaDiffDataBuffer<T> value);
		public abstract ISigmaDiffDataBuffer<T> Map2_F_V_V(MapOp mapOp, FSharpFunc<T, FSharpFunc<T, T>> function, ISigmaDiffDataBuffer<T> a, ISigmaDiffDataBuffer<T> b);
		public abstract ShapedDataBufferView<T> Map_F_M(MapOp mapOp, FSharpFunc<T, T> function, ShapedDataBufferView<T> value);
		public abstract ShapedDataBufferView<T> Map_F_S_M(T other, MapOp mapOp, FSharpFunc<T, T> function, ShapedDataBufferView<T> value);
		public abstract ShapedDataBufferView<T> Map2_F_M_M(MapOp mapOp, FSharpFunc<T, FSharpFunc<T, T>> function, ShapedDataBufferView<T> a, ShapedDataBufferView<T> b);

		/// <summary>
		/// Called before this object is serialised.
		/// </summary>
		public void OnSerialising()
		{
		}

		/// <summary>
		/// Called after this object was serialised.
		/// </summary>
		public void OnSerialised()
		{
		}

		/// <summary>
		/// Called after this object was de-serialised. 
		/// </summary>
		public void OnDeserialised()
		{
			_bufferedSessionArrays = new Dictionary<int, IList<T[]>>();
			_currentSessionArrays = new Dictionary<int, IList<T[]>>();
		}
	}
}
