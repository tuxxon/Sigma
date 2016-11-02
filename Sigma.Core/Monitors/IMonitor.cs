﻿using System;

namespace Sigma.Core.Monitors
{
	public interface IMonitor : IDisposable
	{
		/// <summary>
		/// This function will be called before the start of the monitor.
		/// </summary>
		void Initialise();

		/// <summary>
		/// In this function, the <see cref="IMonitor"/> should start.
		/// If the <see cref="IMonitor"/> runs in a new <see cref="Thread"/> , this function should block until fully up and running. 
		/// </summary>
		void Start();
	}
}
