﻿// <eddie_source_header>
// This file is part of Eddie/AirVPN software.
// Copyright (C)2014-2016 AirVPN (support@airvpn.org) / https://airvpn.org
//
// Eddie is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Eddie is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Eddie. If not, see <http://www.gnu.org/licenses/>.
// </eddie_source_header>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.NetworkInformation;
using System.Management;
using System.Security.Principal;
using System.Xml;
using System.Text;
using System.Threading;
using Eddie.Lib.Common;
using Eddie.Core;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Eddie.Platform.Windows
{
	public class Platform : Core.Platform
	{
		private List<NetworkManagerDhcpEntry> m_listOldDhcp = new List<NetworkManagerDhcpEntry>();
		private List<NetworkManagerDnsEntry> m_listOldDns = new List<NetworkManagerDnsEntry>();
		private object m_oldIpV6 = null;
		private string m_oldMetricInterface = "";
		private int m_oldMetricIPv4 = -1; 
		private int m_oldMetricIPv6 = -1; 
		private Mutex m_mutexSingleInstance = null;
		private Native.ConsoleCtrlHandlerRoutine m_consoleCtrlHandlerRoutine;

		public static bool IsVistaOrNewer()
		{
			OperatingSystem OS = Environment.OSVersion;
			return (OS.Platform == PlatformID.Win32NT) && (OS.Version.Major >= 6);
		}

		public static bool IsWin7OrNewer()
		{
			OperatingSystem OS = Environment.OSVersion;
			return OS.Platform == PlatformID.Win32NT && (OS.Version.Major > 6 || (OS.Version.Major == 6 && OS.Version.Minor >= 1));
		}

		public static bool IsWin8OrNewer()
		{
			OperatingSystem OS = Environment.OSVersion;
			return OS.Platform == PlatformID.Win32NT && (OS.Version.Major > 6 || (OS.Version.Major == 6 && OS.Version.Minor >= 2));
		}

		public static bool IsWin10OrNewer()
		{
			OperatingSystem OS = Environment.OSVersion;
			return (OS.Platform == PlatformID.Win32NT) && (OS.Version.Major >= 10);
		}		
		
		// Override
		public Platform()
		{
			/*
#if EDDIENET20
			// Look the comment in TrustCertificatePolicy.cs
			TrustCertificatePolicy.Activate();
#endif
			*/			
		}

		public override string GetCode()
		{
			return "Windows";
		}

		public override string GetName()
		{
			return Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", "ProductName", "").ToString();
		}

		public override void OnInit(bool cli)
		{
			base.OnInit(cli);

			m_consoleCtrlHandlerRoutine = new Native.ConsoleCtrlHandlerRoutine(ConsoleCtrlCheck); // Avoid Garbage Collector
			Native.SetConsoleCtrlHandler(m_consoleCtrlHandlerRoutine, true);
		}

		public override void OnDeInit()
		{
			base.OnDeInit();

			if (IsVistaOrNewer())
				Wfp.Stop();
		}

		public override void OnStart()
		{
			base.OnStart();

			if (IsVistaOrNewer())
				Wfp.Start();
		}

		public override string GetOsArchitecture()
		{
			if (GetArchitecture() == "x64")
				return "x64";
			if (Native.InternalCheckIsWow64())
				return "x64";
			return "x86";
		}

		public override bool IsAdmin()
		{
			//return true; // Manifest ensure that

			// 2.10.1
			bool isElevated;
			WindowsIdentity identity = WindowsIdentity.GetCurrent();
			WindowsPrincipal principal = new WindowsPrincipal(identity);
			isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

			return isElevated;
		}

		public override bool IsTraySupported()
		{
			return true;
		}

		public override bool GetAutoStart()
		{
			try
			{
				TaskService ts = new TaskService();
				if (ts.RootFolder.Tasks.Exists("AirVPN"))
					return true;				
			}
			catch (NotV1SupportedException)
			{
				//Ignore, not supported on XP
			}

			return false;
		}
		public override bool SetAutoStart(bool value)
		{
			try
			{
				TaskService ts = new TaskService();
				if (value == false)
				{
					if(ts.RootFolder.Tasks.Exists("AirVPN"))
						ts.RootFolder.DeleteTask("AirVPN");
				}
				else
				{
					if(ts.RootFolder.Tasks.Exists("AirVPN"))
						ts.RootFolder.DeleteTask("AirVPN");

					// Create a new task definition and assign properties
					TaskDefinition td = ts.NewTask();
					td.Principal.RunLevel = TaskRunLevel.Highest;					
					//td.Settings.RunOnlyIfLoggedOn = true;
					td.Settings.DisallowStartIfOnBatteries = false;
					td.Settings.StopIfGoingOnBatteries = false;
					td.Settings.RunOnlyIfNetworkAvailable = false;
					td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
					td.Settings.DeleteExpiredTaskAfter = TimeSpan.Zero;
					td.Settings.ExecutionTimeLimit = TimeSpan.Zero;

					td.RegistrationInfo.Description = "AirVPN Client";					
					td.Triggers.Add(new LogonTrigger());
					string command = "\"" + GetExecutablePath() + "\"";
					string arguments = "";
					if (Engine.Instance.Storage.Get("path") != "")
						arguments = "-path=" + Engine.Instance.Storage.Get("path");
					td.Actions.Add(new ExecAction(command, (arguments == "") ? null:arguments, null));

					// Register the task in the root folder
					ts.RootFolder.RegisterTaskDefinition(@"AirVPN", td);
				}
			}
			catch (NotV1SupportedException)
			{
				//Ignore, not supported on XP
			}

			return true;
		}

		public override string NormalizeString(string val)
		{
			return val.Replace("\r\n", "\n").Replace("\n", "\r\n");
		}

		public override string DirSep
		{
			get
			{
				return "\\";
			}
		}

		public override string EndOfLineSep
		{
			get
			{
				return "\r\n";
			}
		}

		public override bool NativeTest()
		{
			return (Native.eddie_test_native() == 3);
		}

		public override string FileGetPhysicalPath(string path)
		{
			string native = Native.GetFinalPathName(path);
			if (native.StartsWith("\\\\?\\"))
				native = native.Substring(4);
			return native;
		}

		public override string GetExecutablePathEx()
		{
			// It return vshost.exe under VS, better
			string path = Environment.GetCommandLineArgs()[0];
			path = Path.GetFullPath(path); // 2.11.9
			return path;
		}        

		public override string GetUserPathEx()
		{
			return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\AirVPN";
		}

		public override string GetDefaultOpenVpnConfigsPath()
		{
			object v = Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\OpenVPN", "config_dir", "");
			if (v == null)
				return "";
			else
				return Conversions.ToString(v);
		}
		
		private bool ConsoleCtrlCheck(Native.CtrlTypes ctrlType)
		{
			switch (ctrlType)
			{
				case Native.CtrlTypes.CTRL_C_EVENT:
					Engine.Instance.OnSignal("C");
					break;
				case Native.CtrlTypes.CTRL_BREAK_EVENT:
					Engine.Instance.OnSignal("BREAK");
					break;
				case Native.CtrlTypes.CTRL_CLOSE_EVENT:
					Engine.Instance.OnSignal("CLOSE");
					break;
				case Native.CtrlTypes.CTRL_LOGOFF_EVENT:
					Engine.Instance.OnSignal("LOGOFF");
					break;
				case Native.CtrlTypes.CTRL_SHUTDOWN_EVENT:
					Engine.Instance.OnSignal("SHUTDOWN");					
					break;
				default:
					break;
			}

			return true;
		}

		public override void ShellCommandDirect(string command, out string path, out string[] arguments)
		{
			path = "cmd.exe";
			arguments = new string[] { "/c", command };
		}

		public override bool ProcessKillSoft(Core.Process process)
		{
			bool result = false;

			if (process != null)
			{
				if (Native.AttachConsole((uint)process.Id))
				{
					Native.SetConsoleCtrlHandler(null, true);

					try
					{
						if (Native.GenerateConsoleCtrlEvent((uint) Native.CtrlTypes.CTRL_C_EVENT, 0))
						{
							process.WaitForExit();
							result = true;
						}
					}
					finally
					{
						Native.FreeConsole();
						Native.SetConsoleCtrlHandler(null, false);
					}
				}
			}

			return result;
		}

		public override int GetRecommendedRcvBufDirective()
		{
			return 256 * 1024;
		}

		public override int GetRecommendedSndBufDirective()
		{
			return 256 * 1024;
		}

		public override void FlushDNS()
		{
			base.FlushDNS();

			// <2.11.8
			//ShellCmd("ipconfig /flushdns");

			// 2.11.10
			if (Engine.Instance.Storage.GetBool("windows.workarounds"))
			{
				SystemShell.ShellCmd("net stop dnscache");
				SystemShell.ShellCmd("net start dnscache");
			}

			SystemShell.ShellCmd("ipconfig /flushdns");
			SystemShell.ShellCmd("ipconfig /registerdns");
		}

		public override void RouteAdd(RouteEntry r)
		{
			string cmd = "";
			cmd += "route add " + r.Address.Address + " mask " + r.Mask.Address + " " + r.Gateway.Address;
			/*
			 * Metric param are ignored or misinterpreted. http://serverfault.com/questions/238695/how-can-i-set-the-metric-of-a-manually-added-route-on-windows
			if(r.Metrics != "")
				cmd += " metric " + r.Metrics;
			*/
			if (r.Interface != "")
				cmd += " if " + r.Interface;
			SystemShell.ShellCmd(cmd); // IJTF2 // TOCHECK
		}

		public override void RouteRemove(RouteEntry r)
		{
			string cmd = "route delete " + r.Address.Address + " mask " + r.Mask.Address + " " + r.Gateway.Address;
			SystemShell.ShellCmd(cmd); // IJTF2 // TOCHECK
		}
		
		public override IpAddresses DetectDNS()
		{
			IpAddresses list = new IpAddresses();

			NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

			foreach (NetworkInterface networkInterface in networkInterfaces)
			{
				if (networkInterface.OperationalStatus == OperationalStatus.Up)
				{
					IPInterfaceProperties ipProperties = networkInterface.GetIPProperties();
					IPAddressCollection dnsAddresses = ipProperties.DnsAddresses;

					foreach (IPAddress dnsAddress in dnsAddresses)
					{
						if(dnsAddress.IsIPv6SiteLocal == false)
							list.Add(dnsAddress.ToString());
					}
				}
			}			

			return list;
		}

		public override bool WaitTunReady()
		{
			int tickStart = Environment.TickCount;
			string lastStatus = "";

			for (; ; )
			{
				NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
				foreach (NetworkInterface adapter in interfaces)
				{
					if (adapter.Description.ToLowerInvariant().StartsWith("tap-win"))
					{
						if (adapter.OperationalStatus == OperationalStatus.Up)
							return true;
						else
						{
							lastStatus = adapter.OperationalStatus.ToString();							
						}
					}
				}

				if (Environment.TickCount - tickStart > 10000)
				{
					Engine.Instance.Logs.Log(LogType.Warning, "Tunnel not ready in 10 seconds, contact our support. Last interface status: " + lastStatus);
					return false;
				}

				Engine.Instance.Logs.Log(LogType.Warning, "Waiting TUN interface");

				System.Threading.Thread.Sleep(2000);
			}
		}

		public override List<RouteEntry> RouteList()
		{			
			List<RouteEntry> entryList = new List<RouteEntry>();

			// 'route print' show IP in Interface fields, but 'route add' need the interface ID. We use a map.
			Dictionary<string, string> InterfacesIp2Id = new Dictionary<string, string>();

			ManagementClass objMC = new ManagementClass("Win32_NetworkAdapterConfiguration");
			ManagementObjectCollection objMOC = objMC.GetInstances();

			foreach (ManagementObject objMO in objMOC)
			{				
				if (!((bool)objMO["IPEnabled"]))
					continue;

				string[] ips = Conversions.ToString(objMO["IPAddress"]).Trim().Split(',');
				string id = Conversions.ToString(objMO["InterfaceIndex"]).Trim();

				foreach (string ip in ips)
				{
					InterfacesIp2Id[ip.Trim()] = id;
				}
			}

			// Loopback interface it's not in the enumeration below.
			InterfacesIp2Id["127.0.0.1"] = "1";

			string cmd = "route PRINT";
			string result = SystemShell.ShellCmd(cmd);

			string[] lines = result.Split('\n');
			foreach (string line in lines)
			{
				string[] fields = Utils.StringCleanSpace(line).Split(' ');

				if(fields.Length == 5)
				{
					// On-link are addresses that can be resolved locally. They don't need a gateway because they don't need to be routed.
					if (fields[2].ToLowerInvariant() == "on-link")
						continue;

					// Route line.
					RouteEntry e = new RouteEntry();
					e.Address = fields[0];
					e.Mask = fields[1];
					e.Gateway = fields[2];
					e.Interface = fields[3];
					e.Metrics = fields[4];
			 
					if(e.Address.Valid == false)
						continue;
					if(e.Mask.Valid == false)
						continue;
					if (e.Gateway.Valid == false)
						continue;
					
					if (InterfacesIp2Id.ContainsKey(e.Interface))
					{
						e.Interface = InterfacesIp2Id[e.Interface];
						entryList.Add(e);
					}
					else
					{
						Engine.Instance.Logs.LogDebug("Unexpected.");
					}
				}
			}
			
			return entryList;
		}

		public override void OnReport(Report report)
		{
			base.OnReport(report);

			report.Add("ipconfig /all", SystemShell.ShellCmd("ipconfig /all"));
			report.Add("route print", SystemShell.ShellCmd("route print"));

			/* // Too much data, generally useless
			t += "\n-- NetworkAdapterConfiguration\n";

			ManagementClass objMC = new ManagementClass("Win32_NetworkAdapterConfiguration");
			ManagementObjectCollection objMOC = objMC.GetInstances();

			foreach (ManagementObject objMO in objMOC)
			{	
				t += "\n";
				t += "Network Adapter: " + Conversions.ToString(objMO.Properties["Caption"].Value) + "\n";
				t += "DNS: " + Conversions.ToString(objMO.Properties["DNSServerSearchOrder"].Value) + "\n";
				
				t += "Details:\n";
				foreach (PropertyData prop in objMO.Properties)
				{
					t += "\t" + prop.Name + ": " + Conversions.ToString(prop.Value) + "\n";					
				}				
			}
			*/
		}

		public override bool OnCheckSingleInstance()
		{
			m_mutexSingleInstance = new Mutex(false, "Global\\" + "b57887e0-65d0-4d18-b57f-106de6e0f1b6");            
			if (m_mutexSingleInstance.WaitOne(0, false) == false)
				return false;
			else
				return true;
		}

		public override void OnNetworkLockManagerInit()
		{
			base.OnNetworkLockManagerInit();

			if (IsVistaOrNewer()) // 2.10.1
			{
				Engine.Instance.NetworkLockManager.AddPlugin(new NetworkLockWfp());
				Engine.Instance.NetworkLockManager.AddPlugin(new NetworkLockWindowsFirewall());				
			}
		}

		public override string OnNetworkLockRecommendedMode()
		{
			if (IsVistaOrNewer() == false)
				return "none";

			if (Engine.Instance.Storage.GetBool("windows.wfp.enable"))
				return "windows_wfp";
			else
				return "windows_firewall";
		}

		public override void OnSessionStart()
		{
			// https://airvpn.org/topic/11162-airvpn-client-advanced-features/ -> Switch DHCP to Static			
			if (Engine.Instance.Storage.GetBool("windows.dhcp_disable"))
				SwitchToStaticDo();

			// FlushDNS(); // Removed in 2.11.10

			Recovery.Save();
		}

		public override void OnSessionStop()
		{			
			SwitchToStaticRestore();

			// FlushDNS(); // Moved in 2.13.3 in ::session.cs, but only if a connection occur.

			Recovery.Save();
		}

		public override bool OnIpV6Do()
		{
			if (Engine.Instance.Storage.GetLower("ipv6.mode") == "disable")
			{
				if ((IsVistaOrNewer()) && (Engine.Instance.Storage.GetBool("windows.wfp.enable")) )
				{
					{
						XmlDocument xmlDocRule = new XmlDocument();
						XmlElement xmlRule = xmlDocRule.CreateElement("rule");
						xmlRule.SetAttribute("name", "IPv6 - Block");
						xmlRule.SetAttribute("layer", "ipv6");
						xmlRule.SetAttribute("action", "block");
						xmlRule.SetAttribute("weight", "3000");
						Wfp.AddItem("ipv6_block_all", xmlRule);
					}

					{
						XmlDocument xmlDocRule = new XmlDocument();
						XmlElement xmlRule = xmlDocRule.CreateElement("rule");
						xmlRule.SetAttribute("name", "IPv6 - Allow loopback");
						xmlRule.SetAttribute("layer", "ipv6");
						xmlRule.SetAttribute("action", "permit");
						xmlRule.SetAttribute("weight", "3001");
						XmlElement XmlIf1 = xmlDocRule.CreateElement("if");
						xmlRule.AppendChild(XmlIf1);
						XmlIf1.SetAttribute("field", "ip_local_interface");
						XmlIf1.SetAttribute("match", "equal");
						XmlIf1.SetAttribute("interface", "loopback");
						Wfp.AddItem("ipv6_allow_loopback", xmlRule);
					}                    

					// <2.12.1
					/*
					XmlDocument xmlDocRule = new XmlDocument();
					XmlElement xmlRule = xmlDocRule.CreateElement("rule");
					xmlRule.SetAttribute("name", "IPv6 - Block");
					xmlRule.SetAttribute("layer", "ipv6");
					xmlRule.SetAttribute("action", "block");
					xmlRule.SetAttribute("weight", "3000"); 
					XmlElement XmlIf1 = xmlDocRule.CreateElement("if");
					xmlRule.AppendChild(XmlIf1);
					XmlIf1.SetAttribute("field", "ip_local_interface");
					XmlIf1.SetAttribute("match", "not_equal");
					XmlIf1.SetAttribute("interface", "loopback");
					Wfp.AddItem("ipv6_block_all", xmlRule);
					*/

					Engine.Instance.Logs.Log(LogType.Verbose, Messages.IpV6DisabledWpf);
				}
				
				if(Engine.Instance.Storage.GetBool("windows.ipv6.os_disable"))
				{
					// http://support.microsoft.com/kb/929852

					m_oldIpV6 = Registry.GetValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\services\\TCPIP6\\Parameters", "DisabledComponents", "");
					if (Conversions.ToUInt32(m_oldIpV6, 0) == 0) // 0 is the Windows default if the key doesn't exist.
						m_oldIpV6 = 0;

					if (Conversions.ToUInt32(m_oldIpV6, 0) == 17) // Nothing to do
					{
						m_oldIpV6 = null;
					}
					else
					{
						UInt32 newValue = 17;
						Registry.SetValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\services\\TCPIP6\\Parameters", "DisabledComponents", newValue, RegistryValueKind.DWord);

						Engine.Instance.Logs.Log(LogType.Verbose, Messages.IpV6DisabledOs);

						Recovery.Save();
					}
				}

				base.OnIpV6Do();
			}

			return true;
		}

		public override bool OnIpV6Restore()
		{
			if (m_oldIpV6 != null)
			{
				Registry.SetValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\services\\TCPIP6\\Parameters", "DisabledComponents", m_oldIpV6, RegistryValueKind.DWord);
				m_oldIpV6 = null;

				Engine.Instance.Logs.Log(LogType.Verbose, Messages.IpV6RestoredOs);
			}


			if( (Wfp.RemoveItem("ipv6_block_all")) && (Wfp.RemoveItem("ipv6_allow_loopback")) )
				Engine.Instance.Logs.Log(LogType.Verbose, Messages.IpV6RestoredWpf);

			base.OnIpV6Restore();

			return true;
		}

		public override bool OnDnsSwitchDo(IpAddresses dns)
		{
			if ((Engine.Instance.Storage.GetBool("windows.dns.lock")) && (IsVistaOrNewer()) && (Engine.Instance.Storage.GetBool("windows.wfp.enable")))                
			{
				// Order is important! IPv6 block use weight 3000, DNS-Lock 2000, WFP 1000. All within a parent filter of max priority.
				// Otherwise the netlock allow-private rule can allow DNS outside the tunnel in some configuration.
				{
					XmlDocument xmlDocRule = new XmlDocument();
					XmlElement xmlRule = xmlDocRule.CreateElement("rule");
					xmlRule.SetAttribute("name", "Dns - Block port 53");
					xmlRule.SetAttribute("layer", "all");
					xmlRule.SetAttribute("action", "block");
					xmlRule.SetAttribute("weight", "2000"); 
					XmlElement XmlIf1 = xmlDocRule.CreateElement("if");
					xmlRule.AppendChild(XmlIf1);
					XmlIf1.SetAttribute("field", "ip_remote_port");
					XmlIf1.SetAttribute("match", "equal");
					XmlIf1.SetAttribute("port", "53");
					Wfp.AddItem("dns_block_all", xmlRule);
				}

				// This is not required yet, but will be required in Eddie 3.                
				{
					XmlDocument xmlDocRule = new XmlDocument();
					XmlElement xmlRule = xmlDocRule.CreateElement("rule");
					xmlRule.SetAttribute("name", "Dns - Allow port 53 of OpenVPN");
					xmlRule.SetAttribute("layer", "all");
					xmlRule.SetAttribute("action", "permit");
					xmlRule.SetAttribute("weight", "2000"); 
					XmlElement XmlIf1 = xmlDocRule.CreateElement("if");
					xmlRule.AppendChild(XmlIf1);
					XmlIf1.SetAttribute("field", "ip_remote_port");
					XmlIf1.SetAttribute("match", "equal");
					XmlIf1.SetAttribute("port", "53");
					XmlElement XmlIf2 = xmlDocRule.CreateElement("if");
					xmlRule.AppendChild(XmlIf2);
					XmlIf2.SetAttribute("field", "ale_app_id");
					XmlIf2.SetAttribute("match", "equal");
					XmlIf2.SetAttribute("path", Software.GetTool("openvpn").Path);
					Wfp.AddItem("dns_permit_openvpn", xmlRule);
				}

				{
					// TOFIX: Missing IPv6 equivalent. Must be done in future when IPv6 support is well tested.
					// Remember: May fail at WFP side with a "Unknown interface" because network interface with IPv6 disabled have Ipv6IfIndex == 0.
					XmlDocument xmlDocRule = new XmlDocument();
					XmlElement xmlRule = xmlDocRule.CreateElement("rule");
					xmlRule.SetAttribute("name", "Dns - Allow port 53 on TAP - IPv4");
					xmlRule.SetAttribute("layer", "ipv4");
					xmlRule.SetAttribute("action", "permit");
					xmlRule.SetAttribute("weight", "2000"); 
					XmlElement XmlIf1 = xmlDocRule.CreateElement("if");
					xmlRule.AppendChild(XmlIf1);
					XmlIf1.SetAttribute("field", "ip_remote_port");
					XmlIf1.SetAttribute("match", "equal");
					XmlIf1.SetAttribute("port", "53");
					XmlElement XmlIf2 = xmlDocRule.CreateElement("if");
					xmlRule.AppendChild(XmlIf2);
					XmlIf2.SetAttribute("field", "ip_local_interface");
					XmlIf2.SetAttribute("match", "equal");
					XmlIf2.SetAttribute("interface", Engine.Instance.ConnectedVpnInterfaceId);
					Wfp.AddItem("dns_permit_tap", xmlRule);
				}                
				

				Engine.Instance.Logs.Log(LogType.Verbose, Messages.DnsLockActivatedWpf);
			}

			string mode = Engine.Instance.Storage.GetLower("dns.mode");
			
			if (mode == "auto")
			{
				try
				{
					ManagementClass objMC = new ManagementClass("Win32_NetworkAdapterConfiguration");
					ManagementObjectCollection objMOC = objMC.GetInstances();

					foreach (ManagementObject objMO in objMOC)
					{
						/*
						if (!((bool)objMO["IPEnabled"]))
							continue;
						*/
						string guid = objMO["SettingID"] as string;

						bool skip = true;

						if((Engine.Instance.Storage.GetBool("windows.dns.lock")) && (Engine.Instance.Storage.GetBool("windows.dns.force_all_interfaces")) )
							skip = false;
						if (guid == Engine.Instance.ConnectedVpnInterfaceId)
							skip = false;

						if (skip == false)
						{
							bool ipEnabled = (bool)objMO["IPEnabled"];

							NetworkManagerDnsEntry entry = new NetworkManagerDnsEntry();

							entry.Guid = guid;
							entry.Description = objMO["Description"] as string;
							entry.Dns = objMO["DNSServerSearchOrder"] as string[];

							entry.AutoDns = ((Registry.GetValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + entry.Guid, "NameServer", "") as string) == "");

							if (entry.Dns == null)
							{
								continue;
							}

							if (entry.AutoDns == false) // Added 2.11
							{
								if (dns.Equals(new IpAddresses(entry.Dns)))
									continue;
							}

							bool done = false;
							
							// 2.13.3 : Win10 with ManagementObject & SetDNSServerSearchOrder sometime return errcode 84 "IP not enabled on adapter" if we try to set DNS on Tap in state "Network cable unplugged", typical in end of session.
							if (IsWin10OrNewer())
							{
								NetworkInterface inet = GetNetworkInterfaceFromGuid(guid);
								if(inet != null)
								{
									string interfaceName = inet.Name;
									int i = 0;
									foreach (IpAddress ip in dns.IPs)
									{
										if (ip.IsV6) // IPv6 TOFIX
											continue;

										if (i == 0)
											SystemShell.ShellCmd("netsh interface ipv4 set dns name=\"" + interfaceName + "\" source=static address=" + ip.Address + " register=primary validate=no");
										else
											SystemShell.ShellCmd("netsh interface ipv4 add dnsserver name=\"" + interfaceName + "\" address=" + ip.Address + " validate=no");
										i++;
									}
									done = true;
								}								
							}
							else
							{
								ManagementBaseObject objSetDNSServerSearchOrder = objMO.GetMethodParameters("SetDNSServerSearchOrder");
								objSetDNSServerSearchOrder["DNSServerSearchOrder"] = dns.AddressesToStringArray();
								objMO.InvokeMethod("SetDNSServerSearchOrder", objSetDNSServerSearchOrder, null);
								done = true;
							}

							if (done)
							{
								m_listOldDns.Add(entry);

								string descFrom = (entry.AutoDns ? "automatic" : "manual") + " (" + String.Join(",", entry.Dns) + ")";
								Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterDnsDone, entry.Description, descFrom, dns.Addresses));
							}							
						}
					}
				}
				catch (Exception e)
				{
					Engine.Instance.Logs.Log(e);
				}

				Recovery.Save();
			}

			base.OnDnsSwitchDo(dns);

			return true;
		}

		public override bool OnDnsSwitchRestore()
		{
			DnsForceRestore();

			bool DnsPermitExists = false;
			DnsPermitExists = DnsPermitExists | Wfp.RemoveItem("dns_permit_openvpn");
			DnsPermitExists = DnsPermitExists | Wfp.RemoveItem("dns_permit_tap");
			DnsPermitExists = DnsPermitExists | Wfp.RemoveItem("dns_block_all");
			if (DnsPermitExists)
				Engine.Instance.Logs.Log(LogType.Verbose, Messages.DnsLockDeactivatedWpf);

			base.OnDnsSwitchRestore();

			return true;
		}

		public override bool OnInterfaceDo(string id)
		{
			int interfaceMetricIPv4Value = Engine.Instance.Storage.GetInt("windows.metrics.tap.ipv4");
			int interfaceMetricIPv6Value = Engine.Instance.Storage.GetInt("windows.metrics.tap.ipv6");
			if( (interfaceMetricIPv4Value == -1) || (interfaceMetricIPv6Value == -1) )
				return true;

			if (interfaceMetricIPv4Value == -2) // Automatic/Recommended
				interfaceMetricIPv4Value = 3;
			if (interfaceMetricIPv6Value == -2) // Automatic/Recommended
				interfaceMetricIPv6Value = 3;

			int interfaceMetricIPv4Idx = -1;
			string interfaceMetricIPv4Name = "";
			int interfaceMetricIPv6Idx = -1;
			string interfaceMetricIPv6Name = "";

			NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface adapter in interfaces)
			{				
				if (adapter.Id == id)
				{
					if (interfaceMetricIPv4Value != -1)
					{
						try
						{
							interfaceMetricIPv4Idx = adapter.GetIPProperties().GetIPv4Properties().Index;
							interfaceMetricIPv4Name = adapter.Name;
						}						
						catch
						{
							// Throw System.Net.NetworkInformation.NetworkInformationException: 
							// 'The requested protocol has not been configured into the system, or no implementation for it exists'
							// if IPv4 it's disabled in the adapter.
						}
					}
					if (interfaceMetricIPv6Value != -1)
					{
						try
						{
							interfaceMetricIPv6Idx = adapter.GetIPProperties().GetIPv6Properties().Index;
							interfaceMetricIPv6Name = adapter.Name;
						}
						catch
						{
							// Throw System.Net.NetworkInformation.NetworkInformationException: 
							// 'The requested protocol has not been configured into the system, or no implementation for it exists'
							// if IPv6 it's disabled in the adapter.
						}
					}
					break;
				}					
			}

			int interfaceMetricIPv4Current = -1;
			int interfaceMetricIPv6Current = -1;

			if (interfaceMetricIPv4Idx != -1)
				interfaceMetricIPv4Current = Native.GetInterfaceMetric(interfaceMetricIPv4Idx, "ipv4");
			if (interfaceMetricIPv6Idx != -1)
				interfaceMetricIPv6Current = Native.GetInterfaceMetric(interfaceMetricIPv6Idx, "ipv6");

			if ( (interfaceMetricIPv4Current != -1) && (interfaceMetricIPv4Current != interfaceMetricIPv4Value) )
			{
				string fromStr = (interfaceMetricIPv4Current == 0) ? "Automatic" : interfaceMetricIPv4Current.ToString();
				string toStr = (interfaceMetricIPv4Value == 0) ? "Automatic" : interfaceMetricIPv4Value.ToString();
				Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterMetricSwitch, interfaceMetricIPv4Name, fromStr, toStr, "IPv4"));
				m_oldMetricInterface = id;
				m_oldMetricIPv4 = interfaceMetricIPv4Current;
				Native.SetInterfaceMetric(interfaceMetricIPv4Idx, "ipv4", interfaceMetricIPv4Value);

				Recovery.Save();
			}

			if ((interfaceMetricIPv6Current != -1) && (interfaceMetricIPv6Current != interfaceMetricIPv6Value))
			{
				string fromStr = (interfaceMetricIPv6Current == 0) ? "Automatic" : interfaceMetricIPv6Current.ToString();
				string toStr = (interfaceMetricIPv6Value == 0) ? "Automatic" : interfaceMetricIPv6Value.ToString();
				Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterMetricSwitch, interfaceMetricIPv6Name, fromStr, toStr, "IPv6"));
				m_oldMetricInterface = id;
				m_oldMetricIPv6 = interfaceMetricIPv6Current;
				Native.SetInterfaceMetric(interfaceMetricIPv6Idx, "ipv6", interfaceMetricIPv6Value);

				Recovery.Save();
			}

			return base.OnInterfaceDo(id);
		}

		public override bool OnInterfaceRestore()
		{
			if(m_oldMetricInterface != "")
			{
				NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
				foreach (NetworkInterface adapter in interfaces)
				{
					if (adapter.Id == m_oldMetricInterface)
					{
						if(m_oldMetricIPv4 != -1)
						{
							int idx = adapter.GetIPProperties().GetIPv4Properties().Index;
							int current = Native.GetInterfaceMetric(idx, "ipv4");
							if (current != m_oldMetricIPv4)
							{
								string fromStr = (current == 0) ? "Automatic" : current.ToString();
								string toStr = (m_oldMetricIPv4 == 0) ? "Automatic" : m_oldMetricIPv4.ToString();
								Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterMetricRestore, adapter.Name, fromStr, toStr, "IPv4"));
								Native.SetInterfaceMetric(idx, "ipv4", m_oldMetricIPv4);
							}
							m_oldMetricIPv4 = -1;
						}

						if (m_oldMetricIPv6 != -1)
						{
							int idx = adapter.GetIPProperties().GetIPv6Properties().Index;
							int current = Native.GetInterfaceMetric(idx, "ipv6");
							if (current != m_oldMetricIPv6)
							{
								string fromStr = (current == 0) ? "Automatic" : current.ToString();
								string toStr = (m_oldMetricIPv6 == 0) ? "Automatic" : m_oldMetricIPv6.ToString();
								Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterMetricRestore, adapter.Name, fromStr, toStr, "IPv6"));
								Native.SetInterfaceMetric(idx, "ipv6", m_oldMetricIPv6);
							}
							m_oldMetricIPv6 = -1;
						}

						m_oldMetricInterface = "";
						break;
					}
				}
			}
			return base.OnInterfaceRestore();
		}

		public override void OnDaemonOutput(string source, string message)
		{
			if (message.IndexOf("Waiting for TUN/TAP interface to come up") != -1)
			{
				if (Engine.Instance.Storage.GetBool("windows.tap_up"))
				{
					HackWindowsInterfaceUp();
				}
			}
		}

		public override void OnRecovery()
		{
			base.OnRecovery();

			if (IsWin7OrNewer()) // 2.12.2
				if (Wfp.ClearPendingRules())
					Engine.Instance.Logs.Log(LogType.Warning, Messages.WfpRecovery);
		}

		public override void OnRecoveryLoad(XmlElement root)
		{
			XmlElement nodeDhcp = Utils.XmlGetFirstElementByTagName(root, "DhcpSwitch");
			if (nodeDhcp != null)
			{
				foreach (XmlElement nodeEntry in nodeDhcp.ChildNodes)
				{
					NetworkManagerDhcpEntry entry = new NetworkManagerDhcpEntry();
					entry.ReadXML(nodeEntry);
					m_listOldDhcp.Add(entry);
				}
			}

			XmlElement nodeDns = Utils.XmlGetFirstElementByTagName(root, "DnsSwitch");
			if (nodeDns != null)
			{
				foreach (XmlElement nodeEntry in nodeDns.ChildNodes)
				{
					NetworkManagerDnsEntry entry = new NetworkManagerDnsEntry();
					entry.ReadXML(nodeEntry);
					m_listOldDns.Add(entry);
				}
			}

			if (Utils.XmlExistsAttribute(root, "IpV6"))
			{
				m_oldIpV6 = Conversions.ToUInt32(Utils.XmlGetAttributeInt64(root, "IpV6", 0), 0);
			}

			if (Utils.XmlExistsAttribute(root, "interface-metric-id"))
			{
				m_oldMetricInterface = Utils.XmlGetAttributeString(root, "interface-metric-id", "");
				m_oldMetricIPv4 = Utils.XmlGetAttributeInt(root, "interface-metric-ipv4", -1);
				m_oldMetricIPv6 = Utils.XmlGetAttributeInt(root, "interface-metric-ipv6", -1);
			}

			SwitchToStaticRestore();
			
			base.OnRecoveryLoad(root);			
		}

		public override void OnRecoverySave(XmlElement root)
		{
			base.OnRecoverySave(root);

			XmlDocument doc = root.OwnerDocument;

			if (m_listOldDhcp.Count != 0)
			{
				XmlElement nodeDhcp = (XmlElement)root.AppendChild(doc.CreateElement("DhcpSwitch"));
				foreach (NetworkManagerDhcpEntry entry in m_listOldDhcp)
				{
					XmlElement nodeEntry = nodeDhcp.AppendChild(doc.CreateElement("entry")) as XmlElement;
					entry.WriteXML(nodeEntry);
				}
			}

			if (m_listOldDns.Count != 0)
			{
				XmlElement nodeDns = (XmlElement)root.AppendChild(doc.CreateElement("DnsSwitch"));
				foreach (NetworkManagerDnsEntry entry in m_listOldDns)
				{
					XmlElement nodeEntry = nodeDns.AppendChild(doc.CreateElement("entry")) as XmlElement;
					entry.WriteXML(nodeEntry);
				}
			}

			if (m_oldIpV6 != null)
				Utils.XmlSetAttributeInt64(root, "IpV6", Conversions.ToInt64(m_oldIpV6));

			if(m_oldMetricInterface != "")
			{
				Utils.XmlSetAttributeString(root, "interface-metric-id", m_oldMetricInterface);
				Utils.XmlSetAttributeInt(root, "interface-metric-ipv4", m_oldMetricIPv4);
				Utils.XmlSetAttributeInt(root, "interface-metric-ipv6", m_oldMetricIPv6);
			}
		}

		private string GetDriverUninstallPath()
		{
			// Note: 32 bit uninstaller can't be viewed by 64 bit app and viceversa.
			// http://www.rhyous.com/2011/01/24/how-read-the-64-bit-registry-from-a-32-bit-application-or-vice-versa/
			
			object objUninstallPath = Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\TAP-Windows", "UninstallString", "");
			if (objUninstallPath != null)
			{
				string uninstallPath = objUninstallPath as string;

				if (Platform.Instance.FileExists(uninstallPath))
					return uninstallPath;
			}

			return "";
		}

		private string GetDriverVersion()
		{
			try
			{
				string regPath = "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\services\\" + Engine.Instance.Storage.Get("windows.adapter_service");
				
				object objSysPath = Registry.GetValue(regPath, "ImagePath", "");
				if (objSysPath != null)
				{
					string sysPath = objSysPath as string;

					if (sysPath.StartsWith("\\SystemRoot\\")) // Win 8 and above
					{
						sysPath = Platform.Instance.NormalizePath(sysPath.Replace("\\SystemRoot\\", Environment.GetEnvironmentVariable("windir") + Platform.Instance.DirSep));
					}
					else // Relative path, Win 7 and below
					{
						sysPath = Platform.Instance.NormalizePath(Environment.GetEnvironmentVariable("windir") + Platform.Instance.DirSep + sysPath);
					}

					if ((GetArchitecture() == "x86") && (GetOsArchitecture() == "x64") && (IsVistaOrNewer()))
					{
						// If Eddie is compiled for 32 bit, and architecture is 64 bit, 
						// tunnel driver path above is real, but Redirector
						// https://msdn.microsoft.com/en-us/library/aa384187(v=vs.85).aspx
						// cause issue.
						// The Sysnative alias was added starting with Windows Vista.
						sysPath = sysPath.ToLowerInvariant();
						sysPath = sysPath.Replace("\\system32\\", "\\sysnative\\");
					}

					if (Platform.Instance.FileExists(sysPath) == false)
					{
						throw new Exception(MessagesFormatter.Format(Messages.OsDriverNoPath, sysPath));
					}

					// GetVersionInfo may throw a FileNotFound exception between 32bit/64bit SO/App.
					FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(sysPath);

					string result = versionInfo.ProductVersion;
					if (result.IndexOf(" ") != -1)
						result = result.Substring(0, result.IndexOf(" "));

					return result;
				}
				else
				{
					throw new Exception(Messages.OsDriverNoRegPath);					
				}

				/* // Not a realtime solution
				ManagementObjectSearcher objSearcher = new ManagementObjectSearcher("Select * from Win32_PnPSignedDriver where DeviceName='" + Engine.Instance.Storage.Get("windows.adapter_name") + "'");

				ManagementObjectCollection objCollection = objSearcher.Get();

				foreach (ManagementObject obj in objCollection)
				{
					object objVersion = obj["DriverVersion"];
					if(objVersion != null)
					{
						string version = objVersion as string;
						return version;
					}				
				}
				*/
			}
			catch (Exception e)
			{
				Engine.Instance.Logs.Log(e);				
			}

			return "";
		}

		public override string GetDriverAvailable()
		{
			string result = "";
			NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface adapter in interfaces)
			{
				// Changed in 2.10.4

				// TAP-Win32 Adapter V9
				// or
				// TAP-Windows Adapter V9
				// or something that start with TAP-Win under XP
				if (adapter.Description.ToLowerInvariant().StartsWith("tap-win"))
				{
					result = adapter.Description;
					break;
				}                
			}

			// Remember: uninstalling OpenVPN doesn't remove tap0901.sys, so finding an adapter is mandatory.
			if (result == "")
			{
				Engine.Instance.Logs.Log(LogType.Verbose, Messages.OsDriverNoAdapterFound);
				return "";
			}
			
			string version = GetDriverVersion();

			if (version == "")
			{
				Engine.Instance.Logs.Log(LogType.Verbose, Messages.OsDriverNoVersionFound);
				return "";
			}

			string bundleVersion = Constants.WindowsDriverVersion;
			if (IsVistaOrNewer() == false) // XP
				bundleVersion = Constants.WindowsXpDriverVersion;

			bool needReinstall = false;

			if(Engine.Instance.Storage.GetBool("windows.disable_driver_upgrade") == false)
				needReinstall = (Utils.CompareVersions(version, bundleVersion) == -1);

			if (needReinstall)
			{
				Engine.Instance.Logs.Log(LogType.Warning, MessagesFormatter.Format(Messages.OsDriverNeedUpgrade, version, bundleVersion));
				return "";
			}

			if (result != "")
				result += ", version ";
			result += version;

			return result;
		}

		public override bool CanInstallDriver()
		{
			string driverPath = GetDriverInstallerPath();
			
			return Platform.Instance.FileExists(driverPath);
		}

		public override bool CanUnInstallDriver()
		{
			if (GetDriverUninstallPath() != "")
				return true;
			
			return false;
		}

		public string GetDriverInstallerPath()
		{
			if(IsVistaOrNewer())
				return Software.FindResource("tap-windows");
			else
				return Software.FindResource("tap-windows-xp");
		}

		public override void InstallDriver()
		{
			string driverPath = GetDriverInstallerPath();

			if (driverPath == "")
				throw new Exception(Messages.OsDriverInstallerNotAvailable);

			SystemShell.Shell1(driverPath, "/S");

			System.Threading.Thread.Sleep(3000);
		}

		public override void UnInstallDriver()
		{
			string uninstallPath = GetDriverUninstallPath();
			if (uninstallPath == "")
				return;

			SystemShell.Shell1(uninstallPath, "/S");

			System.Threading.Thread.Sleep(3000);
		}

		
		// Specific
		public bool IsWindows8()
		{
			if ((Environment.OSVersion.Version.Major == 6) && (Environment.OSVersion.Version.Minor == 2))
				return true;

			return false;
		}

		public void HackWindowsInterfaceUp()
		{
			NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface adapter in interfaces)
			{
				if (adapter.Description.ToLowerInvariant().StartsWith("tap-win"))
				{
					Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.HackInterfaceUpDone, adapter.Name));
					SystemShell.ShellCmd("netsh interface set interface \"" + SystemShell.EscapeInsideQuote(adapter.Name) + "\" ENABLED"); // IJTF2 // TOCHECK
				}
			}            
		}

		/*
		private void WaitUntilEnabled(ManagementObject objMOC)
		{		
			NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface adapter in interfaces)
			{
				if (objMOC["SettingID"] as string == adapter.Id)
				{
					OperationalStatus status = adapter.OperationalStatus;
					if (status == OperationalStatus.Up)
					{
						// TODO: It's always up...
					}
				}
			}
		}
		*/

		private bool SwitchToStaticDo()
		{
			try
			{
				ManagementClass objMC = new ManagementClass("Win32_NetworkAdapterConfiguration");
				ManagementObjectCollection objMOC = objMC.GetInstances();

				foreach (ManagementObject objMO in objMOC)
				{
					if (!((bool)objMO["IPEnabled"]))
						continue;
					if (!((bool)objMO["DHCPEnabled"]))
						continue;

					NetworkManagerDhcpEntry entry = new NetworkManagerDhcpEntry();

					entry.Guid = objMO["SettingID"] as string;
					entry.Description = objMO["Description"] as string;
					entry.IpAddress = (objMO["IPAddress"] as string[]);
					entry.SubnetMask = (objMO["IPSubnet"] as string[]);
					entry.Dns = objMO["DNSServerSearchOrder"] as string[];
					entry.Gateway = objMO["DefaultIPGateway"] as string[];
					
					entry.AutoDns = ((Registry.GetValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + entry.Guid, "NameServer", "") as string) == "");

					Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterDhcpDone, entry.Description));
					
					ManagementBaseObject objEnableStatic = objMO.GetMethodParameters("EnableStatic");
					//objNewIP["IPAddress"] = new string[] { ipAddress };
					//objNewIP["SubnetMask"] = new string[] { subnetMask };
					objEnableStatic["IPAddress"] = new string[] { entry.IpAddress[0] };
					objEnableStatic["SubnetMask"] = new string[] { entry.SubnetMask[0] };
					objMO.InvokeMethod("EnableStatic", objEnableStatic, null);

					ManagementBaseObject objSetDNSServerSearchOrder = objMO.GetMethodParameters("SetDNSServerSearchOrder");
					objSetDNSServerSearchOrder["DNSServerSearchOrder"] = entry.Dns;
					objMO.InvokeMethod("SetDNSServerSearchOrder", objSetDNSServerSearchOrder, null);

					ManagementBaseObject objSetGateways = objMO.GetMethodParameters("SetGateways");
					objSetGateways["DefaultIPGateway"] = new string[] { entry.Gateway[0] };
					objMO.InvokeMethod("SetGateways", objSetGateways, null);

					m_listOldDhcp.Add(entry);
					
					// TOOPTIMIZE: Need to wait until the interface return UP...
					//WaitUntilEnabled(objMO); // Checking the OperationalStatus changes are not effective.
					System.Threading.Thread.Sleep(3000);
				}

				return true;
			}
			catch (Exception e)
			{
				Engine.Instance.Logs.Log(e);
				return false;
			}
		}

		private void SwitchToStaticRestore()
		{
			try
			{
				ManagementClass objMC = new ManagementClass("Win32_NetworkAdapterConfiguration");
				ManagementObjectCollection objMOC = objMC.GetInstances();

				foreach (ManagementObject objMO in objMOC)
				{
					string guid = objMO["SettingID"] as string;
					foreach (NetworkManagerDhcpEntry entry in m_listOldDhcp)
					{
						if (entry.Guid == guid)
						{
							ManagementBaseObject objEnableDHCP = objMO.GetMethodParameters("EnableDHCP");
							objMO.InvokeMethod("EnableDHCP", objEnableDHCP, null);

							ManagementBaseObject objSetDNSServerSearchOrder = objMO.GetMethodParameters("SetDNSServerSearchOrder");
							if (entry.AutoDns == false)
							{
								objSetDNSServerSearchOrder["DNSServerSearchOrder"] = entry.Dns;
							}
							objMO.InvokeMethod("SetDNSServerSearchOrder", objSetDNSServerSearchOrder, null);

							Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterDhcpRestored, entry.Description));
						}
					}
				}
			}
			catch (Exception e)
			{
				Engine.Instance.Logs.Log(e);
			}

			m_listOldDhcp.Clear();
		}

		private void DnsForceRestore()
		{
			try
			{
				// 2.13.3 : Win10 with ManagementObject & SetDNSServerSearchOrder sometime return errcode 84 "IP not enabled on adapter" if we try to set DNS on Tap in state "Network cable unplugged", typical in end of session.
				if (IsWin10OrNewer())
				{
					foreach (NetworkManagerDnsEntry entry in m_listOldDns)
					{
						NetworkInterface inet = GetNetworkInterfaceFromGuid(entry.Guid);
						if (inet != null)
						{
							string interfaceName = SystemShell.EscapeInsideQuote(inet.Name);

							if (entry.AutoDns == true)
							{
								SystemShell.ShellCmd("netsh interface ipv4 set dns name=\"" + interfaceName + "\" source=dhcp register=primary validate=no");
							}
							else
							{
								int i = 0;
								foreach (IpAddress ip in entry.Dns)
								{
									if (ip.IsV6) // IPv6 TOFIX
										continue;

									if (i == 0)
										SystemShell.ShellCmd("netsh interface ipv4 set dns name=\"" + interfaceName + "\" source=static address=" + ip.Address + " register=primary validate=no");
									else
										SystemShell.ShellCmd("netsh interface ipv4 add dnsserver name=\"" + interfaceName + "\" address=" + ip.Address + " validate=no");
									i++;
								}
							}

							string descTo = (entry.AutoDns ? "automatic" : String.Join(",", entry.Dns));
							Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterDnsRestored, entry.Description, descTo));
						}
					}
				}
				else
				{
					ManagementClass objMC = new ManagementClass("Win32_NetworkAdapterConfiguration");
					ManagementObjectCollection objMOC = objMC.GetInstances();

					foreach (ManagementObject objMO in objMOC)
					{
						string guid = objMO["SettingID"] as string;
						foreach (NetworkManagerDnsEntry entry in m_listOldDns)
						{
							if (entry.Guid == guid)
							{
								ManagementBaseObject objSetDNSServerSearchOrder = objMO.GetMethodParameters("SetDNSServerSearchOrder");
								if (entry.AutoDns == false)
								{
									objSetDNSServerSearchOrder["DNSServerSearchOrder"] = entry.Dns;
								}
								else
								{
									//objSetDNSServerSearchOrder["DNSServerSearchOrder"] = new string[] { };
									objSetDNSServerSearchOrder["DNSServerSearchOrder"] = null;
								}

								objMO.InvokeMethod("SetDNSServerSearchOrder", objSetDNSServerSearchOrder, null);

								if (entry.AutoDns == true)
								{
									// Sometime, under Windows 10, the above method don't set it to automatic. So, registry write.
									Registry.SetValue("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + entry.Guid, "NameServer", "");
								}

								string descTo = (entry.AutoDns ? "automatic" : String.Join(",", entry.Dns));
								Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.NetworkAdapterDnsRestored, entry.Description, descTo));
							}
						}
					}
				}
			}
			catch (Exception e)
			{
				Engine.Instance.Logs.Log(e);
			}

			m_listOldDns.Clear();
		}

		private NetworkInterface GetNetworkInterfaceFromGuid(string guid)
		{
			NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface i in interfaces)
			{
				if (i.Id == guid)
					return i;
			}
			return null;
		}
	}

	public class NetworkManagerDhcpEntry
	{
		//public ManagementObject Obj;
		public string Description;
		public string[] IpAddress;
		public string[] SubnetMask;
		public string[] Dns;
		public string[] Gateway;
		public string Guid;
		public bool AutoDns;

		public void ReadXML(XmlElement node)
		{
			Description = Utils.XmlGetAttributeString(node, "description", "");
			IpAddress = Utils.XmlGetAttributeStringArray(node, "ip_address");
			SubnetMask = Utils.XmlGetAttributeStringArray(node, "subnet_mask");
			Dns = Utils.XmlGetAttributeStringArray(node, "dns");
			Gateway = Utils.XmlGetAttributeStringArray(node, "gateway");
			Guid = Utils.XmlGetAttributeString(node, "guid", "");
			AutoDns = Utils.XmlGetAttributeBool(node, "auto_def", false);
		}

		public void WriteXML(XmlElement node)
		{
			Utils.XmlSetAttributeString(node, "description", Description);
			Utils.XmlSetAttributeStringArray(node, "ip_address", IpAddress);
			Utils.XmlSetAttributeStringArray(node, "subnet_mask", SubnetMask);
			Utils.XmlSetAttributeStringArray(node, "dns", Dns);
			Utils.XmlSetAttributeStringArray(node, "gateway", Gateway);
			Utils.XmlSetAttributeString(node, "guid", Guid);
			Utils.XmlSetAttributeBool(node, "auto_dns", AutoDns);			
		}
	}

	public class NetworkManagerDnsEntry
	{
		//public ManagementObject Obj;
		public string Description;
		public string[] Dns;
		public string Guid;
		public bool AutoDns;

		public void ReadXML(XmlElement node)
		{
			Description = Utils.XmlGetAttributeString(node, "description", "");
			Dns = Utils.XmlGetAttributeStringArray(node, "dns");
			Guid = Utils.XmlGetAttributeString(node, "guid", "");
			AutoDns = Utils.XmlGetAttributeBool(node, "auto_def", false);
		}

		public void WriteXML(XmlElement node)
		{
			Utils.XmlSetAttributeString(node, "description", Description);
			Utils.XmlSetAttributeStringArray(node, "dns", Dns);
			Utils.XmlSetAttributeString(node, "guid", Guid);
			Utils.XmlSetAttributeBool(node, "auto_dns", AutoDns);			
		}
	}
}
