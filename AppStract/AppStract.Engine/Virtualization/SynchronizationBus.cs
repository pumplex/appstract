﻿#region Copyright (C) 2009-2010 Simon Allaeys

/*
    Copyright (C) 2009-2010 Simon Allaeys
 
    This file is part of AppStract

    AppStract is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    AppStract is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with AppStract.  If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.Collections.Generic;
using System.Threading;
using AppStract.Engine.Configuration;
using AppStract.Engine.Data.Connection;
using AppStract.Engine.Data.Databases;
using AppStract.Engine.Virtualization.FileSystem;
using AppStract.Engine.Virtualization.Registry;
using AppStract.Utilities.Data;
using AppStract.Utilities.Observables;

namespace AppStract.Engine.Virtualization
{
  /// <summary>
  /// Synchronizes queuries with the host process.
  /// </summary>
  /// <remarks>
  /// <see cref="DatabaseAction{T}"/>s are enqueued until <see cref="Flush"/> is called, which flushes them as a single batch to the host process.
  /// When <see cref="AutoFlush"/> is set to true, queries are flushed every time <see cref="FlushInterval"/> passes.
  /// <br />
  /// If the <see cref="SynchronizationBus"/> detects that the process is queried to shut down,
  /// the queues are automatically flushed to the <see cref="IProcessSynchronizer"/> of the host process.
  /// </remarks>
  internal sealed class SynchronizationBus : IDisposable, IFileSystemSynchronizer, IRegistrySynchronizer
  {

    #region Variables

    /// <summary>
    /// The <see cref="IConfigurationProvider"/> to use for loading the resources.
    /// </summary>
    private readonly IConfigurationProvider _loader;

    private readonly RegistryDatabase _regDatabase;
    /// <summary>
    /// The object to lock when performing actions
    /// related to <see cref="_flushInterval"/> and/or <see cref="_autoFlush"/>.
    /// </summary>
    private readonly object _flushSyncObject;
    /// <summary>
    /// The interval between each call to <see cref="Flush"/>,
    /// in milliseconds.
    /// </summary>
    private int _flushInterval;
    /// <summary>
    /// Whether the enqueued data must be automatically flushed.
    /// </summary>
    private bool _autoFlush;

    #endregion

    #region Properties

    /// <summary>
    /// Gets or sets whether the enqueued <see cref="DatabaseAction{T}"/>s must be automatically flushed
    /// each time <see cref="FlushInterval"/> has passed.
    /// Default value is false.
    /// </summary>
    public bool AutoFlush
    {
      get { return _autoFlush; }
      set
      {
        lock (_flushSyncObject)
        {
          if (_autoFlush == value)
            return;
          _autoFlush = value;
          if (_autoFlush)
            new Thread(StartFlushing) {IsBackground = true, Name = "CommBus"}.Start();
        }
      }
    }

    /// <summary>
    /// Gets or sets the interval between each call to <see cref="Flush"/>, in milliseconds.
    /// </summary>
    /// <remarks>
    /// The default interval is 500 milliseconds.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An <see cref="ArgumentOutOfRangeException"/> is thrown if the interval specified
    /// is not equal to or greater than 0.
    /// </exception>
    public int FlushInterval
    {
      get { return _flushInterval; }
      set
      {
        if (value < 0)
          throw new ArgumentOutOfRangeException("value", "The FlushInterval specified must be greater than -1.");
        _flushInterval = value;
      }
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of <see cref="SynchronizationBus"/>.
    /// </summary>
    /// <param name="configurationProvider">
    /// The <see cref="IConfigurationProvider"/> to use for configuring the bus.
    /// </param>
    public SynchronizationBus(IConfigurationProvider configurationProvider)
    {
      _loader = configurationProvider;
      if (configurationProvider.ConnectionStrings.ContainsKey(ConfigurationDataType.RegistryDatabase))
        _regDatabase = new RegistryDatabase(configurationProvider.ConnectionStrings[ConfigurationDataType.RegistryDatabase]);
      else if (configurationProvider.ConnectionStrings.ContainsKey(ConfigurationDataType.RegistryDatabaseFile))
        _regDatabase = RegistryDatabase.CreateDefaultDatabase(configurationProvider.ConnectionStrings[ConfigurationDataType.RegistryDatabaseFile]);
      else
        throw new ConfigurationDataException(ConfigurationDataType.RegistryDatabase);
      _regDatabase.Initialize();
      _autoFlush = false;
      _flushInterval = 500;
      _flushSyncObject = new object();
      EngineCore.OnProcessExit += GuestCore_OnProcessExit;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Flushes all enqueued items to the <see cref="IProcessSynchronizer"/> 
    /// attached to the current <see cref="SynchronizationBus"/> instance.
    /// </summary>
    public void Flush()
    {
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Starts flushing.
    /// This method doesn't return unless <see cref="_autoFlush"/> is set to false.
    /// </summary>
    private void StartFlushing()
    {
      while (true)
      {
        int flushInterval;
        lock (_flushSyncObject)
        {
          if (!_autoFlush)
            return;
          Flush();
          if (!_autoFlush)
            return;
          flushInterval = _flushInterval;
        }
        Thread.Sleep(flushInterval);
      }
    }

    /// <summary>
    /// Eventhandler for <see cref="EngineCore.OnProcessExit"/>.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void GuestCore_OnProcessExit(object sender, EventArgs e)
    {
      if (_autoFlush)
        Flush();
    }

    /// <summary>
    /// Eventhandler for the ItemAdded event of the registry.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="item"></param>
    /// <param name="args"></param>
    private void Registry_ItemAdded(ICollection<KeyValuePair<uint, VirtualRegistryKey>> sender, KeyValuePair<uint, VirtualRegistryKey> item, EventArgs args)
    {
      _regDatabase.EnqueueAction(new DatabaseAction<VirtualRegistryKey>(item.Value, DatabaseActionType.Set));
    }

    /// <summary>
    /// Eventhandler for the ItemChanged event of the registry.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="item"></param>
    /// <param name="args"></param>
    private void Registry_ItemChanged(ICollection<KeyValuePair<uint, VirtualRegistryKey>> sender, KeyValuePair<uint, VirtualRegistryKey> item, EventArgs args)
    {
      _regDatabase.EnqueueAction(new DatabaseAction<VirtualRegistryKey>(item.Value, DatabaseActionType.Set));
    }

    /// <summary>
    /// Eventhandler for the ItemRemoved event of the registry.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="item"></param>
    /// <param name="args"></param>
    private void Registry_ItemRemoved(ICollection<KeyValuePair<uint, VirtualRegistryKey>> sender, KeyValuePair<uint, VirtualRegistryKey> item, EventArgs args)
    {
      _regDatabase.EnqueueAction(new DatabaseAction<VirtualRegistryKey>(item.Value, DatabaseActionType.Remove));
    }

    #endregion

    #region IDisposable Members

    public void Dispose()
    {
      EngineCore.OnProcessExit -= GuestCore_OnProcessExit;
    }

    #endregion

    #region IFileSystemSynchronizer Members

    public FileSystemRuleCollection GetFileSystemEngineRules()
    {
      //using (EngineCore.Engine.GetEngineProcessingSpace())
        return _loader.GetFileSystemEngineRules();
    }

    #endregion

    #region IRegistrySynchronizer Members

    public RegistryRuleCollection GetRegistryEngineRules()
    {
      return _loader.GetRegistryEngineRules();
    }

    public void SynchronizeRegistryWith(ObservableDictionary<uint, VirtualRegistryKey> keyList)
    {
      if (keyList == null)
        throw new ArgumentNullException("keyList");
      keyList.Clear();
      var keys = _regDatabase.ReadAll();
      foreach (var key in keys)
        keyList.Add(key.Handle, key);
      keyList.ItemAdded += Registry_ItemAdded;
      keyList.ItemChanged += Registry_ItemChanged;
      keyList.ItemRemoved += Registry_ItemRemoved;
    }

    #endregion

  }
}
