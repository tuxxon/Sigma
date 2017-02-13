﻿/* 
MIT License

Copyright (c) 2016-2017 Florian Cäsar, Michael Plainer

For full license see LICENSE in the root directory of this project. 
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using log4net;
using static Sigma.Core.Utils.ThreadUtils;
using Sigma.Core.Architecture;
using Sigma.Core.Data.Iterators;
using Sigma.Core.Handlers;
using Sigma.Core.Handlers.Backends.SigmaDiff.NativeCpu;
using Sigma.Core.Training.Hooks;
using Sigma.Core.Training.Mergers;
using Sigma.Core.Training.Operators.Workers;
using Sigma.Core.Training.Optimisers;
using Sigma.Core.Utils;

namespace Sigma.Core.Training.Operators
{
	[Serializable]
	public abstract class BaseOperator : IOperator
	{
		/// <summary>
		///		A registry containing relevant parameters of this operator.
		/// </summary>
		public IRegistry Registry { get; }

		/// <summary>
		///     All local <see cref="IHook" />s that are attached to this <see cref="IOperator" />.
		/// </summary>
		public IReadOnlyCollection<IHook> AttachedLocalHooks { get; protected set; }

		/// <summary>
		///     All global <see cref="IHook" />s that are attached to this <see cref="IOperator" />.
		/// </summary>
		public IReadOnlyCollection<IHook> AttachedGlobalHooks { get; protected set; }

		/// <summary>
		///     The <see cref="SigmaEnvironment" /> this operator runs in and communicates with.
		///     It will be automatically set by the <see cref="ITrainer" />.
		/// </summary>
		public SigmaEnvironment Sigma { get; set; }

		/// <summary>
		///     The current <see cref="ExecutionState" /> of the <see cref="IOperator" />. <see cref="ExecutionState.None" />
		///     if the operator has not been started yet.
		/// </summary>
		public ExecutionState State { get; protected set; } = ExecutionState.None;

		/// <summary>
		///     The <see cref="IComputationHandler" /> used to compute everything in
		///     this <see cref="IOperator" />. It will be automatically set by the
		///     <see cref="ITrainer" /> if not specified.
		/// </summary>
		public IComputationHandler Handler { get; set; }

		/// <summary>
		///     The <see cref="ITrainer" /> that is being trained in this operators training process.
		///     This will be automatically set by the corresponding <see cref="ITrainer" />.
		/// </summary>
		public ITrainer Trainer { get; set; }

		/// <summary>
		///     The <see cref="INetwork" /> the training process is operated on.
		///     This will be automatically set by the corresponding <see cref="ITrainer" />.
		/// </summary>
		public INetwork Network { get; set; }

		/// <summary>
		///		This merger is used to merge multiple networks after they get
		///		reported to the <see cref="IOperator"/>. Defaults to <see cref="AverageNetworkMerger"/>.
		/// </summary>
		public INetworkMerger NetworkMerger { get; set; } = new AverageNetworkMerger("layers.*.*"); // merge everything from all layers by default

		/// <summary>
		///     The number of <see cref="IWorker" />s (threads) used in this
		///     <see cref="IOperator" /> in parallel.
		/// </summary>
		public int WorkerCount { get; }

		/// <summary>
		///		The number of the current global epoch in this operator.
		/// </summary>
		public int EpochNumber { get; protected set; }

		/// <summary>
		/// The logger for the inheriting class. 
		/// </summary>
		protected ILog Logger => _logger ?? (_logger = LogManager.GetLogger(GetType()));

		/// <summary>
		///		All local hooks sorted by time scale.
		/// </summary>
		protected readonly IDictionary<TimeScale, ISet<IHook>> LocalHooksByTimeScale;

		/// <summary>
		///		All global hooks sorted by time scale.
		/// </summary>
		protected readonly IDictionary<TimeScale, ISet<IHook>> GlobalHooksByTimescale;

		/// <summary>
		///     All the <see cref="IWorker" />s managed by this operator.
		/// </summary>
		protected IEnumerable<IWorker> Workers;

		/// <summary>
		///		The worker indices by workers for quick access.
		/// </summary>
		protected IReadOnlyDictionary<IWorker, int> WorkerIndicesByWorkers;

		/// <summary>
		/// The logger, which is initialised in the property getter so that the class matches the actual implementation.
		/// </summary>
		private ILog _logger;

		/// <summary>
		/// The lock that will be used to perform asynchronous management of the <see cref="IWorker"/>.
		/// </summary>
		private readonly object _stateChangeLock;

		/// <summary>
		/// The current epoch number, with all networks corresponding to that epoch. 
		/// </summary>
		private readonly IDictionary<int, INetwork[]> _pushedEpochNetworks;

		/// <summary>
		/// The latest pushed local iteration number indexed by worker indices by epoch number.
		/// </summary>
		private Dictionary<int, int[]> _pushedLocalIterationNumbers;

		/// <summary>
		///		The alive hooks by an array of flags of workers keeping it alive.
		/// </summary>
		private readonly IDictionary<IHook, bool[]> _aliveHooksByInWorkerStates;

		// TODO reorder all global / local hook methods, accessors, members and variables to follow local -> global order in declaration---it's annoying

		private readonly IDictionary<IHook, uint> _localHookInvocationIndices;
		private readonly IDictionary<IHook, uint> _globalHookInvocationIndices;
		private readonly IDictionary<IHook, uint> _localHookInvocationTargets;
		private readonly IDictionary<IHook, uint> _globalHookInvocationTargets;
		private readonly IDictionary<IHook, ISet<IHook>> _dependentHooksByRequiredHook;
		private readonly IRegistryResolver _bufferRegistryResolver;
		private readonly IList<IHook> _localHooks;
		private readonly IList<IHook> _globalHooks;
		private readonly ISet<string> _bufferRegistryEntries;
		private readonly ISet<string> _bufferResolvedRegistryEntries;
		private readonly object _networkChangedLock;
		private readonly List<IHook> _bufferHooksToInvoke;
		private readonly IList<IHook> _bufferHooksInBackgroundToInvoke;
		private readonly IDictionary<IHook, ITimeStep> _localGlobalHookTimeSteps;
		private int _highestIterationNumber;

		/// <summary>
		///     Create a new <see cref="BaseOperator" /> using the default <see cref="IComputationHandler" /> (currently <see cref="CpuFloat32Handler"/>.
		///     The <see cref="IComputationHandler" /> will be automatically set by the <see cref="ITrainer" />.
		///		TODO update documentation (?)
		/// </summary>
		/// <param name="workerCount">
		///     The number of <see cref="IWorker" />s (threads) used in this <see cref="IOperator" /> in
		///     parallel.
		/// </param>
		protected BaseOperator(int workerCount) : this(new CpuFloat32Handler(), workerCount)
		{
		}

		/// <summary>
		///     Create a new <see cref="BaseOperator" /> with a specified <see cref="IComputationHandler" />.
		///     The <see cref="IComputationHandler" /> will <c>not</c> be modified by the <see cref="ITrainer" />.
		/// </summary>
		/// <param name="handler">
		///     The <see cref="IComputationHandler" /> that will be assigned to the
		///     <see cref="IComputationHandler" />
		/// </param>
		/// <param name="workerCount">
		///     The number of <see cref="IWorker" />s (threads) used in this <see cref="IOperator" /> in
		///     parallel.
		/// </param>
		protected BaseOperator(IComputationHandler handler, int workerCount)
		{
			if (handler == null) throw new ArgumentNullException(nameof(handler));
			if (workerCount <= 0) throw new ArgumentOutOfRangeException($"{nameof(workerCount)} must be > 0 but was {WorkerCount}.");

			Handler = handler;
			WorkerCount = workerCount;

			Registry = new Registry(tags: "operator");

			_localGlobalHookTimeSteps = new Dictionary<IHook, ITimeStep>();
			_pushedEpochNetworks = new Dictionary<int, INetwork[]>();
			_pushedLocalIterationNumbers = new Dictionary<int, int[]>();
			_globalHooks = new List<IHook>();
			_localHooks = new List<IHook>();
			_bufferRegistryResolver = new RegistryResolver(Registry);
			_bufferRegistryEntries = new HashSet<string>();
			_bufferResolvedRegistryEntries = new HashSet<string>();
			_bufferHooksToInvoke = new List<IHook>();
			_bufferHooksInBackgroundToInvoke = new List<IHook>();
			_localHookInvocationIndices = new Dictionary<IHook, uint>();
			_globalHookInvocationIndices = new Dictionary<IHook, uint>();
			_localHookInvocationTargets = new Dictionary<IHook, uint>();
			_globalHookInvocationTargets = new Dictionary<IHook, uint>();
			_dependentHooksByRequiredHook = new Dictionary<IHook, ISet<IHook>>();
			_networkChangedLock = new object();
			_stateChangeLock = new object();
			_aliveHooksByInWorkerStates = new Dictionary<IHook, bool[]>();

			LocalHooksByTimeScale = new Dictionary<TimeScale, ISet<IHook>>();
			GlobalHooksByTimescale = new Dictionary<TimeScale, ISet<IHook>>();
			AttachedLocalHooks= new ReadOnlyCollection<IHook>(_localHooks);
			AttachedGlobalHooks= new ReadOnlyCollection<IHook>(_globalHooks);
		}

		public void PushProgress(IWorker worker)
		{
			// TODO workers calling this method are assumed to only submit new progress with a different epoch / iteration number, check for that or explicitly state in documentation
			// first iteration of new epoch complete
			if (worker.LocalEpochNumber > EpochNumber && worker.LocalIterationNumber == 1)
			{
				if (PushEpochNetwork(worker))
				{
					EpochNumber++;

					Logger.Debug($"All workers (total of {WorkerCount}) are done with epoch {worker.LocalEpochNumber} in operator {this} and have pushed their network progress for this epoch.");

					MergeWorkerNetworks(EpochNumber);

					lock (_pushedEpochNetworks)
					{
						// remove networks of last epoch to free up memory
						_pushedEpochNetworks[EpochNumber] = null;
					}

					InvokeTimeScaleEvent(TimeScale.Epoch);
				}
			}

			bool allWorkersAtIteration = true;
			lock (_pushedLocalIterationNumbers)
			{
				if (!_pushedLocalIterationNumbers.ContainsKey(worker.LocalEpochNumber))
				{
					_pushedLocalIterationNumbers.Add(worker.LocalEpochNumber, new int[WorkerCount]);
				}

				// check if all workers are at that iteration
				int[] localIterationNumbers = _pushedLocalIterationNumbers[worker.LocalEpochNumber];

				localIterationNumbers[WorkerIndicesByWorkers[worker]] = worker.LocalIterationNumber;

				if (localIterationNumbers.Any(i => i != worker.LocalIterationNumber))
				{
					allWorkersAtIteration = false;
				}
			}

			if (allWorkersAtIteration)
			{
				// if worker is at highest current iteration number, update global iteration
				if (worker.LocalEpochNumber == EpochNumber)
				{
					_highestIterationNumber = worker.LocalIterationNumber;
				}

				InvokeTimeScaleEvent(TimeScale.Iteration);
			}
		}

		protected void InvokeTimeScaleEvent(TimeScale timeScale)
		{
			EjectTimeScaleEvent(timeScale, AttachedGlobalHooks, _localGlobalHookTimeSteps, _bufferHooksToInvoke);

			PopulateRegistry(Registry, Network, Trainer.Optimiser, Trainer.TrainingDataIterator, EpochNumber, _highestIterationNumber);

			ArrayUtils.SortListInPlaceIndexed(_bufferHooksToInvoke, GetGlobalHookInvocationIndex);
			HookUtils.FetchOrderedBackgroundHooks(_bufferHooksToInvoke, _bufferHooksInBackgroundToInvoke);

			foreach (IHook hook in _bufferHooksToInvoke)
			{
				if (!hook.InvokeInBackground)
				{
					hook.Operator = this;
					hook.Invoke(Registry, _bufferRegistryResolver);
				}
			}

			if (_bufferHooksInBackgroundToInvoke.Count > 0)
			{
				DispatchBackgroundHookInvocation(_bufferHooksInBackgroundToInvoke, Registry, _bufferRegistryEntries, _bufferResolvedRegistryEntries);
			}
		}

		public void PullProgress(IWorker worker)
		{
			// before first iteration of new epoch or network has not been initialised yet
			// also only pull if there is more than one worker, otherwise it's pointless
			if (worker.LocalIterationNumber == 0 && WorkerCount > 1 || worker.LocalNetwork == null)
			{
				worker.LocalNetwork = PullNetwork();
			}
		}

		protected virtual INetwork PullNetwork()
		{
			if (Network == null)
			{
				throw new InvalidOperationException($"Cannot pull network before assigning a network to operator {this}.");
			}

			lock (_networkChangedLock)
			{
				return (INetwork) Network.DeepCopy();
			}
		}

		protected virtual bool PushEpochNetwork(IWorker worker)
		{
			bool allNetworksForEpochPushed;

			lock (_pushedEpochNetworks)
			{
				INetwork[] networks = _pushedEpochNetworks.TryGetValue(worker.LocalEpochNumber, () => new INetwork[WorkerCount]);
				if (!networks.AddToNextNull(worker.LocalNetwork.DeepCopy()))
				{
					throw new InvalidOperationException($"Too many workers trying to push their network, worker {worker} attempted to push his network but {WorkerCount} workers already pushed their network for epoch {worker.LocalEpochNumber}.");
				}

				allNetworksForEpochPushed = _pushedEpochNetworks[worker.LocalEpochNumber][WorkerCount - 1] != null;
			}

			Logger.Debug($"Worker {worker.GetType()} pushed its network for the epoch {worker.LocalEpochNumber}.");

			return allNetworksForEpochPushed;
		}

		private void MergeWorkerNetworks(int epochNumber)
		{
			Logger.Debug($"Merging local pushed networks from all workers (total of {WorkerCount}) into global network of operator {this}...");

			lock (_networkChangedLock)
			{
				NetworkMerger.Merge(Network, _pushedEpochNetworks[epochNumber], Handler);
			}

			Logger.Debug($"Done merging local pushed networks from all workers (total of {WorkerCount}) into global network of operator {this}.");
		}

		public bool AttachLocalHook(IHook hook)
		{
			HookUtils.ValidateHook(hook, _localHooks);

			if (_localHooks.Contains(hook))
			{
				// TODO check "Cannot" and "Cannot" logger messages and fix them for consistency
				Logger.Debug($"Cannot attach local hook {hook} to operator {this}, hook is already attached.");

				return false;
			}

			if (_localHooks.Any(existingHook => existingHook.FunctionallyEquals(hook)))
			{
				Logger.Debug($"Cannot attach local hook {hook} to operator {this}, functionally equivalent hook is already attached.");

				return false;
			}

			AttachHook(hook, _localHooks, LocalHooksByTimeScale, AttachLocalHook);

			RebuildHookInvocationCache(_localHooks, _localHookInvocationIndices, _localHookInvocationTargets);

			Logger.Debug($"Attached local hook {hook} to operator {this}.");

			return true;
		}

		public bool DetachLocalHook(IHook hook)
		{
			if (_dependentHooksByRequiredHook.ContainsKey(hook))
			{
				throw new InvalidOperationException($"Cannot detach local hook {hook} from operator {this} because it's required by dependent hook(s) {_dependentHooksByRequiredHook[hook]}.");
			}

			if (!_localHooks.Remove(hook))
			{
				return false;
			}

			DetachHook(hook, LocalHooksByTimeScale, DetachLocalHook);

			RebuildHookInvocationCache(_localHooks, _localHookInvocationIndices, _localHookInvocationTargets);

			Logger.Debug($"Detached local hook {hook} from operator {this}.");

			return true;
		}

		public bool AttachGlobalHook(IHook hook)
		{
			HookUtils.ValidateHook(hook, _globalHooks);

			if (_globalHooks.Contains(hook))
			{
				Logger.Debug($"Cannot attach global hook {hook} to operator {this}, hook is already attached.");

				return false;
			}

			if (_globalHooks.Any(existingHook => existingHook.FunctionallyEquals(hook)))
			{
				Logger.Debug($"Cannot attach global hook {hook} to operator {this}, functionally equivalent hook is already attached.");

				return false;
			}

			AttachHook(hook, _globalHooks, GlobalHooksByTimescale, AttachGlobalHook);
			RebuildHookInvocationCache(_globalHooks, _globalHookInvocationIndices, _globalHookInvocationTargets);

			Logger.Debug($"Attached global hook {hook} to operator {this}.");

			return true;
		}

		public bool DetachGlobalHook(IHook hook)
		{
			if (_dependentHooksByRequiredHook.ContainsKey(hook))
			{
				throw new InvalidOperationException($"Cannot detach global hook {hook} from operator {this} because it's required by dependent hook(s) {_dependentHooksByRequiredHook[hook]}.");
			}

			if (!_globalHooks.Remove(hook))
			{
				return false;
			}

			DetachHook(hook, GlobalHooksByTimescale, DetachGlobalHook);

			RebuildHookInvocationCache(_globalHooks, _globalHookInvocationIndices, _globalHookInvocationTargets);

			Logger.Debug($"Detached global hook {hook} from operator {this}");

			return true;
		}

		private void AttachHook(IHook hook, ICollection<IHook> allHooks, IDictionary<TimeScale, ISet<IHook>> hooksByTimescale, Func<IHook, bool> attachFunction)
		{
			allHooks.Add(hook);

			hooksByTimescale.TryGetValue(hook.TimeStep.TimeScale, () => new HashSet<IHook>()).Add(hook);

			foreach (IHook requiredHook in hook.RequiredHooks)
			{
				// use own required hook if successfully attached (=first) or otherwise get first functionally equal hook and set that as required
				bool attachedOwnRequiredHook = attachFunction.Invoke(requiredHook);
				IHook usedRequiredHook = attachedOwnRequiredHook ? requiredHook : allHooks.First(existingHook => existingHook.FunctionallyEquals(requiredHook));
				_dependentHooksByRequiredHook.TryGetValue(usedRequiredHook, () => new HashSet<IHook>()).Add(hook);
			}
		}

		private void DetachHook(IHook hook, IDictionary<TimeScale, ISet<IHook>> hooksByTimescale, Func<IHook, bool> detachFunction)
		{
			hooksByTimescale[hook.TimeStep.TimeScale].Remove(hook);
			_aliveHooksByInWorkerStates.Remove(hook);

			foreach (IHook requiredHook in hook.RequiredHooks)
			{
				// if the dependent hooks are empty after removing this dependent we can safely detach the child required hook
				if (_dependentHooksByRequiredHook.RemoveAndClean(requiredHook, hook))
				{
					detachFunction.Invoke(requiredHook);
				}
			}
		}

		private static void RebuildHookInvocationCache(IEnumerable<IHook> hooks, IDictionary<IHook, uint> hookInvocationIndices, IDictionary<IHook, uint> hookInvocationTargets)
		{
			hookInvocationIndices.Clear();
			hookInvocationTargets.Clear();

			LinkedList<IHook> invocationOrder = new LinkedList<IHook>();
			ISet<IHook> hooksToTraverse = new HashSet<IHook>(hooks);

			uint invocationTarget = 1;
			while (hooksToTraverse.Count > 0)
			{
				IHook hook = hooksToTraverse.First();

				uint currentInvocationTarget = hook.InvokeInBackground ? invocationTarget++ : 0; // invocation target for foreground is 0
				_InternalTraverseInvocationOrder(hook, currentInvocationTarget, invocationOrder, hooksToTraverse, hookInvocationTargets);
			}

			uint invocationIndex = 0;
			foreach (IHook hook in invocationOrder)
			{
				hookInvocationIndices[hook] = invocationIndex++;
			}
		}

		private static void _InternalTraverseInvocationOrder(IHook hook, uint invocationTarget, LinkedList<IHook> invocationOrder, ICollection<IHook> toTraverse, IDictionary<IHook, uint> invocationTargets)
		{
			foreach (IHook requiredHook in hook.RequiredHooks)
			{
				_InternalTraverseInvocationOrder(requiredHook, invocationTarget, invocationOrder, toTraverse, invocationTargets);
			}

			invocationOrder.AddLast(hook);
			invocationTargets[hook] = invocationTarget;
			toTraverse.Remove(hook);
		}

		/// <summary>
		/// Get the invocation index for a certain local hook. 
		/// This invocation index represents the index at which this operator should be invoked.
		/// Used for ordering hooks to satisfy all dependencies upon invocation.
		/// Note: All hooks with a smaller invocation index and the same invocation target should be invoked before this hook.
		/// </summary>
		/// <param name="hook">The hook.</param>
		/// <returns>The invocation index of the given local hook.</returns>
		public uint GetLocalHookInvocationIndex(IHook hook)
		{
			if (!_localHookInvocationIndices.ContainsKey(hook))
			{
				throw new InvalidOperationException($"Cannot get hook invocation index of unknown local hook {hook} from operator {this} (is the hook attached to this operator?).");
			}

			return _localHookInvocationIndices[hook];
		}

		/// <summary>
		/// Get the invocation target for a certain local hook.
		/// The invocation target represents the thread in which the hook should be invoked.
		/// Used for putting background hooks with dependencies in the right "invocation bucket" for dependency satisfaction.
		/// Note:   Only background hooks require invocation targets.
		///			The invocation target of a foreground hook is implicitly the owning thread. 
		/// </summary>
		/// <param name="hook">The hook.</param>
		/// <returns>The invocation target for the given local hook.</returns>
		public uint GetLocalHookInvocationTarget(IHook hook)
		{
			if (!_localHookInvocationTargets.ContainsKey(hook))
			{
				throw new InvalidOperationException($"Cannot get hook invocation target of unknown local hook {hook} from operator {this} (is the hook attached to this operator?).");
			}

			return _localHookInvocationTargets[hook];
		}

		/// <summary>
		/// Get the invocation index for a certain global hook. 
		/// This invocation index represents the index at which this operator should be invoked.
		/// Used for ordering hooks to satisfy all dependencies upon invocation.
		/// Note: All hooks with a smaller invocation index and the same invocation target should be invoked before this hook.
		/// </summary>
		/// <param name="hook">The hook.</param>
		/// <returns>The invocation index of the given global hook.</returns>
		public uint GetGlobalHookInvocationIndex(IHook hook)
		{
			if (!_globalHookInvocationIndices.ContainsKey(hook))
			{
				throw new InvalidOperationException($"Cannot get hook invocation index of unknown global hook {hook} from operator {this} (is the hook attached to this operator?).");
			}

			return _globalHookInvocationIndices[hook];
		}

		/// <summary>
		/// Get the invocation target for a certain global hook.
		/// The invocation target represents the thread in which the hook should be invoked.
		/// Used for putting background hooks with dependencies in the right "invocation bucket" for dependency satisfaction.
		/// Note:   Only background hooks require invocation targets.
		///			The invocation target of a foreground hook is implicitly the owning thread. 
		/// </summary>
		/// <param name="hook">The hook.</param>
		/// <returns>The invocation target for the given global hook.</returns>
		public uint GetGlobalHookInvocationTarget(IHook hook)
		{
			if (!_globalHookInvocationIndices.ContainsKey(hook))
			{
				throw new InvalidOperationException($"Cannot get hook invocation target of unknown global hook {hook} from operator {this} (is the hook attached to this operator?).");
			}

			return _globalHookInvocationTargets[hook];
		}

		/// <summary>
		/// Mark a local hook as dead in a certain worker.
		/// </summary>
		/// <param name="hook">The hook to mark.</param>
		/// <param name="worker">The worker in which this hook was deemed dead.</param>
		public void MarkHookDead(IHook hook, IWorker worker)
		{
			if (!_aliveHooksByInWorkerStates.ContainsKey(hook))
			{
				throw new InvalidOperationException($"Cannot mark hook {hook} as dead in operator {this} for worker {worker}, hook is not registered as alive.");
			}

			if (!WorkerIndicesByWorkers.ContainsKey(worker))
			{
				throw new InvalidOperationException($"Cannot mark hook {hook} as dead in operator {this} for worker {worker}, worker does not belong to this operator.");
			}

			bool[] aliveFlags = _aliveHooksByInWorkerStates[hook];

			aliveFlags[WorkerIndicesByWorkers[worker]] = false;

			if (aliveFlags.All(flag => !flag))
			{
				Logger.Debug($"Detaching hook {hook} in operator {this}, hook is deemed completely dead and can be safely detached.");

				DetachLocalHook(hook);
			}
		}

		/// <summary>
		/// Eject a certain time scale event within a certain worker and update the local time steps.
		/// </summary>
		/// <param name="timeScale">The time scale.</param>
		/// <param name="hooks">The hooks to check and invoke.</param>
		/// <param name="localHookTimeSteps">The local hook time steps to use (and populate if missing).</param>
		/// <param name="resultHooksToInvoke">The resulting hooks to invoke.</param>
		public void EjectTimeScaleEvent(TimeScale timeScale, IEnumerable<IHook> hooks, IDictionary<IHook, ITimeStep> localHookTimeSteps, List<IHook> resultHooksToInvoke)
		{
			if (timeScale == null) throw new ArgumentNullException(nameof(timeScale));
			if (hooks == null) throw new ArgumentNullException(nameof(hooks));
			if (localHookTimeSteps == null) throw new ArgumentNullException(nameof(localHookTimeSteps));
			if (resultHooksToInvoke == null) throw new ArgumentNullException(nameof(resultHooksToInvoke));

			resultHooksToInvoke.Clear();

			foreach (IHook hook in hooks)
			{
				if (hook.TimeStep.TimeScale != timeScale)
				{
					continue;
				}

				if (!localHookTimeSteps.ContainsKey(hook))
				{
					TimeStep timeStep = (TimeStep) hook.TimeStep.DeepCopy();

					timeStep.LocalLiveTime = timeStep.LiveTime;
					timeStep.LocalInterval = timeStep.Interval;

					localHookTimeSteps.Add(hook, timeStep);
				}

				ITimeStep localTimeStep = localHookTimeSteps[hook];

				if (localTimeStep.LocalLiveTime == 0)
				{
					continue;
				}

				localTimeStep.LocalInterval--;

				if (localTimeStep.LocalInterval == 0)
				{
					resultHooksToInvoke.Add(hook);

					if (localTimeStep.LocalLiveTime > 0)
					{
						localTimeStep.LocalLiveTime--;
					}

					localTimeStep.LocalInterval = localTimeStep.Interval;
				}
			}
		}

		/// <summary>
		/// Dispatch a list of ordered hooks for background invocation. The required registry entries are automatically copied from the given local registry. 
		/// </summary>
		/// <param name="hooksToInvokeInBackground">The hooks to invoke in the background.</param>
		/// <param name="localRegistry">The local registry to copy required registry entries from.</param>
		/// <param name="bufferRegistryEntries"></param>
		/// <param name="bufferResolvedRegistryEntries"></param>
		public void DispatchBackgroundHookInvocation(IList<IHook> hooksToInvokeInBackground, IRegistry localRegistry, ISet<string> bufferRegistryEntries, ISet<string> bufferResolvedRegistryEntries)
		{
			if (hooksToInvokeInBackground.Count <= 0)
			{
				return;
			}

			IRegistry copy = HookUtils.GetRegistryCopyForHooks(localRegistry, hooksToInvokeInBackground, bufferRegistryEntries, bufferResolvedRegistryEntries);
			IRegistryResolver copyResolver = new RegistryResolver(copy);

			foreach (IHook hook in hooksToInvokeInBackground)
			{
				hook.Operator = this;

				// TODO add background hook "bucket" invocation for dependent / required hooks
				System.Threading.Tasks.Task.Factory.StartNew(() => hook.Invoke(copy, copyResolver));
			}
		}

		/// <summary>
		/// This method blocks until the last state change has been fully performed.
		/// Returns immediately if not implemented.
		/// </summary>
		public void WaitForStateChanged()
		{
			lock (_stateChangeLock) { }
		}

		/// <summary>
		/// This method assures that <see cref="Workers"/> is initialised (with <see cref="InitialiseWorkers"/>)
		/// and checks if all required parameters are set. 
		/// </summary>
		protected virtual void PrepareWorkers()
		{
			// TODO: check if all required parameter are set
			// TODO uncomment this code and add more parameter checks
			//if (Trainer == null) throw new InvalidOperationException($"{nameof(Trainer)} cannot be null.");
			//if (Trainer.TrainingDataIterator == null) throw new InvalidOperationException($"{nameof(Trainer.TrainingDataIterator)} cannot be null.");
			//if (NetworkMerger == null) throw new InvalidOperationException($"{nameof(NetworkMerger)} cannot be null.");

			if (Workers == null)
			{
				Workers = InitialiseWorkers();
			}
		}

		/// <summary>
		///     This method creates the <see cref="IWorker" />s. It will be called before the first start of the operator.
		///     The <see cref="IWorker" />s are usually created via <see cref="CreateWorker" />.
		/// </summary>
		/// <returns>An <see cref="IEnumerable{T}" /> with the required amount of <see cref="IWorker" />s.</returns>
		protected virtual IEnumerable<IWorker> InitialiseWorkers()
		{
			IWorker[] workers = new IWorker[WorkerCount];
			IDictionary<IWorker, int> workerIndicesByWorkers = new Dictionary<IWorker, int>();

			for (int i = 0; i < workers.Length; i++)
			{
				workers[i] = CreateWorker();
				workers[i].LocalTrainingDataIterator = Trainer?.TrainingDataIterator?.ShallowCopy(); // TODO remove null conditional access, its only to pass operator/worker tests without trainer
				workers[i].LocalOptimiser = (IOptimiser) Trainer?.Optimiser?.DeepCopy();

				workerIndicesByWorkers.Add(workers[i], i);
			}

			WorkerIndicesByWorkers = new ReadOnlyDictionary<IWorker, int>(workerIndicesByWorkers);
			_pushedLocalIterationNumbers = new Dictionary<int, int[]>();

			return workers;
		}

		/// <summary>
		/// Start all workers with <see cref="StartWorker"/>.
		/// </summary>
		protected virtual void StartWorkers()
		{
			foreach (IWorker worker in Workers)
			{
				StartWorker(worker);
			}
		}

		/// <summary>
		///		Start all workers once (for one iteration) with <see cref="RunWorkerOnce"/>. 
		/// </summary>
		protected virtual void StartWorkersOnce()
		{
			foreach (IWorker worker in Workers)
			{
				RunWorkerOnce(worker);
			}
		}

		#region StateControl

		public virtual void StartOnce()
		{
			if ((State == ExecutionState.None) || (State == ExecutionState.Stopped))
			{
				new BlockingLockingThread(_stateChangeLock, () =>
				{
					PrepareWorkers();

					StartWorkersOnce();

					State = ExecutionState.Running;
				}).Start();
			}
			else
			{
				ThrowBadState("started");
			}
		}

		/// <summary>
		///     Start this operator in a separate thread (return immediately).
		/// </summary>
		/// <exception cref="InvalidOperationException">If the operator is running or paused.</exception>
		public void Start()
		{
			if ((State == ExecutionState.None) || (State == ExecutionState.Stopped))
			{
				new BlockingLockingThread(_stateChangeLock, () =>
				{
					PrepareWorkers();

					StartWorkers();

					State = ExecutionState.Running;
				}).Start();
			}
			else
			{
				ThrowBadState("started");
			}
		}

		/// <summary>
		///     Signal this operator to stop as soon as possible.
		/// </summary>
		/// <exception cref="InvalidOperationException">If the operator is not running.</exception>
		public void SignalPause()
		{
			if (State == ExecutionState.Running)
			{
				new BlockingLockingThread(_stateChangeLock, () =>
				{
					foreach (IWorker worker in Workers) { PauseWorker(worker); }

					State = ExecutionState.Paused;
				}).Start();
			}
			else
			{
				ThrowBadState("paused");
			}
		}

		/// <summary>
		///     Signal this operator to resume as soon as possible.
		/// </summary>
		/// <exception cref="InvalidOperationException">If the operator is not paused.</exception>
		public void SignalResume()
		{
			if (State == ExecutionState.Paused)
			{
				new BlockingLockingThread(_stateChangeLock, () =>
				 {
					 foreach (IWorker worker in Workers) { ResumeWorker(worker); }

					 State = ExecutionState.Running;
				 }).Start();
			}
			else
			{
				ThrowBadState("resumed");
			}
		}

		/// <summary>
		///     Signal this operator to stop as soon as possible.
		/// </summary>
		/// <exception cref="InvalidOperationException">If the operator is already stopped.</exception>
		public void SignalStop()
		{
			if (State != ExecutionState.Stopped)
			{
				new BlockingLockingThread(_stateChangeLock, () =>
				 {
					 foreach (IWorker worker in Workers)
					 {
						 PauseWorker(worker);
						 StopWorker(worker);
					 }

					 State = ExecutionState.Stopped;
				 }).Start();
			}
			else
			{
				ThrowBadState("stopped");
			}
		}

		/// <summary>
		/// </summary>
		/// <param name="currentState"></param>
		/// <exception cref="InvalidOperationException"></exception>
		private void ThrowBadState(string currentState)
		{
			throw new InvalidOperationException($"The operator cannot be {currentState} because the state is: {State}!");
		}

		#endregion

		/// <summary>
		///		Populate a registry using a certain worker's local values.
		/// </summary>
		/// <param name="registry">The registry to populate.</param>
		/// <param name="worker">The worker to fetch local values from.</param>
		public void PopulateWorkerRegistry(IRegistry registry, IWorker worker)
		{
			// TODO create documentation about which registry entries mean what 
			PopulateRegistry(registry, worker.LocalNetwork, worker.LocalOptimiser, worker.LocalTrainingDataIterator, worker.LocalEpochNumber, worker.LocalIterationNumber);
		}

		/// <summary>
		/// Update a given registry with certain local values (typically for workers convenience).
		/// </summary>
		/// <param name="registry">The registry to update.</param>
		/// <param name="localNetwork">The local network.</param>
		/// <param name="localOptimiser">The local optimiser.</param>
		/// <param name="localIterator">The local data iterator.</param>
		/// <param name="localEpochNumber">The local epoch number.</param>
		/// <param name="localIterationNumber">The local iteration number.</param>
		protected void PopulateRegistry(IRegistry registry, INetwork localNetwork, IOptimiser localOptimiser, IDataIterator localIterator,
			int localEpochNumber, int localIterationNumber)
		{
			if (registry == null) throw new ArgumentNullException(nameof(registry));
			if (localNetwork == null) throw new ArgumentNullException(nameof(localNetwork));
			if (localOptimiser == null) throw new ArgumentNullException(nameof(localOptimiser));
			if (localIterator == null) throw new ArgumentNullException(nameof(localIterator));

			registry["network"] = localNetwork.Registry;
			registry["optimiser"] = localOptimiser.Registry;
			registry["iterator"] = localIterator.Registry;
			registry["trainer"] = Trainer.Registry;
			registry["epoch"] = localEpochNumber;
			registry["iteration"] = localIterationNumber;

			if (!registry.ContainsKey("shared") || !(registry["shared"] is IRegistry))
			{
				registry["shared"] = new Registry(parent: registry, tags: "shared");
			}
		}

		#region AbstractWorkerMethods

		/// <summary>
		///     This method creates an <see cref="IWorker" />.
		/// </summary>
		/// <returns>The newly created <see cref="IWorker" />.</returns>
		protected abstract IWorker CreateWorker();

		/// <summary>
		///     This method starts a worker.
		/// </summary>
		/// <param name="worker">The worker that will be started.</param>
		protected abstract void StartWorker(IWorker worker);

		/// <summary>
		///     This method starts a worker for a single iteration.
		/// </summary>
		/// <param name="worker">The worker that will be started.</param>
		protected abstract void RunWorkerOnce(IWorker worker);

		/// <summary>
		///     This method pauses a worker. It will also be
		///     called if the worker is stopped.
		/// </summary>
		/// <param name="worker">The worker that will be paused.</param>
		protected abstract void PauseWorker(IWorker worker);

		/// <summary>
		///     This method resumes a worker from it's paused state.
		/// </summary>
		/// <param name="worker">The worker that will be resumed.</param>
		protected abstract void ResumeWorker(IWorker worker);

		/// <summary>
		///     This method stops a worker. All resources should
		///     be freed.
		/// </summary>
		/// <param name="worker">The worker that will be paused and stopped.</param>
		protected abstract void StopWorker(IWorker worker);

		#endregion AbstractWorkerMethods
	}
}