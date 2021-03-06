#if UNITY_EDITOR && UNITY_EDITORVR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.EditorVR.Menus;
using UnityEditor.Experimental.EditorVR.Modules;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEditor.Experimental.EditorVR.Workspaces;
using UnityEngine;
using UnityEngine.InputNew;

namespace UnityEditor.Experimental.EditorVR.Core
{
	partial class EditorVR
	{
		const float k_MainMenuAutoHideDelay = 0.125f;
		const float k_MainMenuAutoShowDelay = 0.25f;

		[SerializeField]
		MainMenuActivator m_MainMenuActivatorPrefab;

		[SerializeField]
		PinnedToolButton m_PinnedToolButtonPrefab;

		class Menus : Nested, IInterfaceConnector, ILateBindInterfaceMethods<Tools>
		{
			internal class MenuHideData
			{
				public MenuHideFlags hideFlags = MenuHideFlags.Hidden;
				public MenuHideFlags lastHideFlags = MenuHideFlags.Hidden;
				public float autoHideTime;
				public float autoShowTime;
			}

			const float k_MenuHideMargin = 0.075f;
			const float k_TwoHandHideDistance = 0.25f;
			const int k_PossibleOverlaps = 16;

			readonly Dictionary<KeyValuePair<Type, Transform>, ISettingsMenuProvider> m_SettingsMenuProviders = new Dictionary<KeyValuePair<Type, Transform>, ISettingsMenuProvider>();
			readonly Dictionary<KeyValuePair<Type, Transform>, ISettingsMenuItemProvider> m_SettingsMenuItemProviders = new Dictionary<KeyValuePair<Type, Transform>, ISettingsMenuItemProvider>();
			List<Type> m_MainMenuTools;

			// Local method use only -- created here to reduce garbage collection
			readonly List<DeviceData> m_ActiveDeviceData = new List<DeviceData>();
			readonly List<IWorkspace> m_WorkspaceComponents = new List<IWorkspace>();
			readonly Collider[] m_WorkspaceOverlaps = new Collider[k_PossibleOverlaps];

			public Menus()
			{
				IInstantiateMenuUIMethods.instantiateMenuUI = InstantiateMenuUI;
				IIsMainMenuVisibleMethods.isMainMenuVisible = IsMainMenuVisible;
			}

			public void ConnectInterface(object obj, Transform rayOrigin = null)
			{
				var settingsMenuProvider = obj as ISettingsMenuProvider;
				if (settingsMenuProvider != null)
					m_SettingsMenuProviders[new KeyValuePair<Type, Transform>(obj.GetType(), rayOrigin)] = settingsMenuProvider;

				var settingsMenuItemProvider = obj as ISettingsMenuItemProvider;
				if (settingsMenuItemProvider != null)
					m_SettingsMenuItemProviders[new KeyValuePair<Type, Transform>(obj.GetType(), rayOrigin)] = settingsMenuItemProvider;

				var mainMenu = obj as IMainMenu;
				if (mainMenu != null)
				{
					mainMenu.menuTools = m_MainMenuTools;
					mainMenu.menuWorkspaces = WorkspaceModule.workspaceTypes;
					mainMenu.settingsMenuProviders = m_SettingsMenuProviders;
					mainMenu.settingsMenuItemProviders = m_SettingsMenuItemProviders;
				}
			}

			public void DisconnectInterface(object obj, Transform rayOrigin = null)
			{
				var settingsMenuProvider = obj as ISettingsMenuProvider;
				if (settingsMenuProvider != null)
					m_SettingsMenuProviders.Remove(new KeyValuePair<Type, Transform>(obj.GetType(), rayOrigin));

				var settingsMenuItemProvider = obj as ISettingsMenuItemProvider;
				if (settingsMenuItemProvider != null)
					m_SettingsMenuItemProviders.Remove(new KeyValuePair<Type, Transform>(obj.GetType(), rayOrigin));
			}

			public void LateBindInterfaceMethods(Tools provider)
			{
				m_MainMenuTools = provider.allTools.Where(t => !Tools.IsDefaultTool(t)).ToList(); // Don't show tools that can't be selected/toggled
			}

			static void UpdateAlternateMenuForDevice(DeviceData deviceData)
			{
				var alternateMenu = deviceData.alternateMenu;
				alternateMenu.menuHideFlags = deviceData.currentTool is IExclusiveMode ? 0 : deviceData.menuHideData[alternateMenu].hideFlags;

				// Move the activator button to an alternate position if the alternate menu will be shown
				var mainMenuActivator = deviceData.mainMenuActivator;
				if (mainMenuActivator != null)
					mainMenuActivator.activatorButtonMoveAway = alternateMenu.menuHideFlags == 0;
			}

			internal void UpdateMenuVisibilities()
			{
				m_ActiveDeviceData.Clear();
				Rays.ForEachProxyDevice(deviceData =>
				{
					m_ActiveDeviceData.Add(deviceData);
				});

				foreach (var deviceData in m_ActiveDeviceData)
				{
					var alternateMenu = deviceData.alternateMenu;
					var mainMenu = deviceData.mainMenu;
					var customMenu = deviceData.customMenu;
					var menuHideData = deviceData.menuHideData;
					MenuHideData customMenuHideData = null;
					MenuHideData alternateMenuData = null;

					var mainMenuVisible = mainMenu != null && menuHideData[mainMenu].hideFlags == 0;
					var alternateMenuVisible = false;
					if (alternateMenu != null)
					{
						alternateMenuData = menuHideData[alternateMenu];
						alternateMenuVisible = alternateMenuData.hideFlags == 0;
					}

					var customMenuVisible = false;
					if (customMenu != null)
					{
						customMenuHideData = menuHideData[customMenu];
						customMenuVisible = customMenuHideData.hideFlags == 0;
					}

					// Kick the alternate menu to the other hand if a main menu or custom menu is visible
					if (alternateMenuVisible && (mainMenuVisible || customMenuVisible))
					{
						foreach (var otherDeviceData in m_ActiveDeviceData)
						{
							if (otherDeviceData == deviceData)
								continue;

							SetAlternateMenuVisibility(otherDeviceData.rayOrigin, true);
							break;
						}
					}

					// Temporarily hide customMenu if other menus are visible
					if (customMenuVisible && mainMenuVisible)
						customMenuHideData.hideFlags |= MenuHideFlags.OtherMenu;

					// Temporarily hide alternateMenu if other menus are visible
					if (alternateMenuVisible && (customMenuVisible || mainMenuVisible))
						alternateMenuData.hideFlags |= MenuHideFlags.OtherMenu;

					// Check if menu bounds overlap with any workspace colliders
					foreach (var kvp in menuHideData)
					{
						CheckMenuWorkspaceOverlaps(kvp.Key, kvp.Value);
					}

					// Check if there are currently any held objects, or if the other hand is in proximity for scaling
					CheckDirectSelection(deviceData, menuHideData, alternateMenuVisible);
				}

				// Set show/hide timings
				foreach (var deviceData in m_ActiveDeviceData)
				{
					foreach (var kvp in deviceData.menuHideData)
					{
						var hideFlags = kvp.Value.hideFlags;
						if ((hideFlags & ~MenuHideFlags.Hidden & ~MenuHideFlags.OtherMenu) == 0)
							kvp.Value.autoHideTime = Time.time;

						if (hideFlags != 0)
						{
							var menuHideData = kvp.Value;
							menuHideData.lastHideFlags = menuHideData.hideFlags;
							kvp.Value.autoShowTime = Time.time;
						}
					}
				}

				// Apply MenuHideFlags to UI visibility
				foreach (var deviceData in m_ActiveDeviceData)
				{
					var mainMenu = deviceData.mainMenu;
					var mainMenuHideData = deviceData.menuHideData[mainMenu];
					var mainMenuHideFlags = mainMenuHideData.hideFlags;
					var lastMainMenuHideFlags = mainMenuHideData.lastHideFlags;

					var permanentlyHidden = (mainMenuHideFlags & MenuHideFlags.Hidden) != 0;
					var wasPermanentlyHidden = (lastMainMenuHideFlags & MenuHideFlags.Hidden) != 0;
					//Temporary states take effect after a delay
					var temporarilyHidden = (mainMenuHideFlags & MenuHideFlags.Temporary) != 0
						&& Time.time > mainMenuHideData.autoHideTime + k_MainMenuAutoHideDelay;
					var wasTemporarilyHidden = (lastMainMenuHideFlags & MenuHideFlags.Temporary) != 0
						&& Time.time > mainMenuHideData.autoShowTime + k_MainMenuAutoShowDelay;

					// If the menu is focused, only hide if Hidden is set (e.g. not temporary) in order to hide the selected tool
					if (permanentlyHidden || wasPermanentlyHidden || !mainMenu.focus && (temporarilyHidden || wasTemporarilyHidden))
						mainMenu.menuHideFlags = mainMenuHideFlags;

					// Disable the main menu activator if any temporary states are set
					deviceData.mainMenuActivator.interactable = (mainMenuHideFlags & MenuHideFlags.Temporary) == 0;

					// Show/hide custom menu, if it exists
					var customMenu = deviceData.customMenu;
					if (customMenu != null)
						customMenu.menuHideFlags = deviceData.menuHideData[customMenu].hideFlags;

					UpdateAlternateMenuForDevice(deviceData);
					Rays.UpdateRayForDevice(deviceData, deviceData.rayOrigin);
				}

				// Reset Temporary states and set lastHideFlags
				foreach (var deviceData in m_ActiveDeviceData)
				{
					foreach (var kvp in deviceData.menuHideData)
					{
						kvp.Value.hideFlags &= ~MenuHideFlags.Temporary;
					}
				}

				evr.GetModule<DeviceInputModule>().UpdatePlayerHandleMaps();
			}

			void CheckDirectSelection(DeviceData deviceData, Dictionary<IMenu, MenuHideData> menuHideData, bool alternateMenuVisible)
			{
				var viewerScale = Viewer.GetViewerScale();
				var directSelection = evr.GetNestedModule<DirectSelection>();
				var rayOrigin = deviceData.rayOrigin;
				var rayOriginPosition = rayOrigin.position;
				var heldObjects = directSelection.GetHeldObjects(rayOrigin);
				// If this hand is holding any objects, hide its menus
				var hasDirectSelection = heldObjects != null && heldObjects.Count > 0;
				if (hasDirectSelection)
				{
					foreach (var kvp in menuHideData)
					{
						// Only set if hidden--value is reset every frame
						kvp.Value.hideFlags |= MenuHideFlags.HasDirectSelection;
					}

					foreach (var otherDeviceData in m_ActiveDeviceData)
					{
						if (otherDeviceData == deviceData)
							continue;

						var otherRayOrigin = otherDeviceData.rayOrigin;
						if (alternateMenuVisible && otherDeviceData.alternateMenu != null)
							SetAlternateMenuVisibility(otherRayOrigin, true);

						// If other hand is within range to do a two-handed scale, hide its menu as well
						if (directSelection.IsHovering(otherRayOrigin) || directSelection.IsScaling(otherRayOrigin)
							|| Vector3.Distance(otherRayOrigin.position, rayOriginPosition) < k_TwoHandHideDistance * viewerScale)
						{
							foreach (var kvp in otherDeviceData.menuHideData)
							{
								// Only set if hidden--value is reset every frame
								kvp.Value.hideFlags |= MenuHideFlags.HasDirectSelection;
							}
							break;
						}
					}
				}
			}

			void CheckMenuWorkspaceOverlaps(IMenu menu, MenuHideData menuHideData)
			{
				var menuBounds = menu.localBounds;
				if (menuBounds.extents == Vector3.zero)
					return;

				Array.Clear(m_WorkspaceOverlaps, 0, m_WorkspaceOverlaps.Length);
				var hoveringWorkspace = false;
				var menuTransform = menu.menuContent.transform;
				var menuRotation = menuTransform.rotation;
				var viewerScale = Viewer.GetViewerScale();
				var center = menuTransform.position + menuRotation * menuBounds.center * viewerScale;
				if (Physics.OverlapBoxNonAlloc(center, menuBounds.extents * viewerScale, m_WorkspaceOverlaps, menuRotation) > 0)
				{
					foreach (var overlap in m_WorkspaceOverlaps)
					{
						if (overlap)
						{
							m_WorkspaceComponents.Clear();
							overlap.GetComponents(m_WorkspaceComponents);
							if (m_WorkspaceComponents.Count > 0)
								hoveringWorkspace = true;
						}
					}
				}

				// Only set if hidden--value is reset every frame
				if (hoveringWorkspace)
					menuHideData.hideFlags |= MenuHideFlags.OverWorkspace;
			}

			internal static bool IsValidHover(MultipleRayInputModule.RaycastSource source)
			{
				var go = source.draggedObject;
				if (!go)
					go = source.hoveredObject;

				if (!go)
					return true;

				if (go == evr.gameObject)
					return true;

				var eventData = source.eventData;
				var rayOrigin = eventData.rayOrigin;
				var deviceData = evr.m_DeviceData.FirstOrDefault(dd => dd.rayOrigin == rayOrigin);
				if (deviceData != null)
				{
					if (go.transform.IsChildOf(deviceData.rayOrigin)) // Don't let UI on this hand block the menu
						return false;

					var scaledPointerDistance = eventData.pointerCurrentRaycast.distance / Viewer.GetViewerScale();
					var menuHideFlags = deviceData.menuHideData;
					var mainMenu = deviceData.mainMenu;
					IMenu openMenu = mainMenu;
					if (deviceData.customMenu != null && menuHideFlags[mainMenu].hideFlags != 0)
						openMenu = deviceData.customMenu;

					if (scaledPointerDistance < openMenu.localBounds.size.y + k_MenuHideMargin)
					{
						// Only set if hidden--value is reset every frame
						menuHideFlags[openMenu].hideFlags |= MenuHideFlags.OverUI;
						return true;
					}

					return (menuHideFlags[openMenu].hideFlags & MenuHideFlags.Hidden) != 0;
				}

				return true;
			}

			internal static void UpdateAlternateMenuOnSelectionChanged(Transform rayOrigin)
			{
				if (rayOrigin == null)
					return;

				SetAlternateMenuVisibility(rayOrigin, Selection.gameObjects.Length > 0);
			}

			internal static void SetAlternateMenuVisibility(Transform rayOrigin, bool visible)
			{
				Rays.ForEachProxyDevice(deviceData =>
				{
					var menuHideFlags = deviceData.menuHideData;
					var alternateMenu = deviceData.alternateMenu;
					if (alternateMenu != null)
					{
						// Set alternate menu visible on this rayOrigin and hide it on all others
						var alternateMenuData = menuHideFlags[alternateMenu];
						if (deviceData.rayOrigin == rayOrigin && visible)
							alternateMenuData.hideFlags &=  ~MenuHideFlags.Hidden;
						else
							alternateMenuData.hideFlags |= MenuHideFlags.Hidden;
					}
				});
			}

			internal static void OnMainMenuActivatorSelected(Transform rayOrigin, Transform targetRayOrigin)
			{
				if (evr.m_DeviceData.Any(deviceData => deviceData.rayOrigin == rayOrigin
					&& !deviceData.mainMenuActivator.interactable))
					return;

				foreach (var deviceData in evr.m_DeviceData)
				{
					var mainMenu = deviceData.mainMenu;
					if (mainMenu != null)
					{
						var customMenu = deviceData.customMenu;
						var alternateMenu = deviceData.alternateMenu;
						var menuHideData = deviceData.menuHideData;
						var mainMenuHideData = menuHideData[mainMenu];
						var alternateMenuVisible = alternateMenu != null
							&& (menuHideData[alternateMenu].hideFlags & MenuHideFlags.Hidden) == 0;

						// Do not delay when showing via activator
						mainMenuHideData.autoShowTime = 0;

						if (deviceData.rayOrigin == rayOrigin)
						{
							// Toggle main menu hidden flag
							mainMenuHideData.hideFlags ^= MenuHideFlags.Hidden;
							mainMenu.targetRayOrigin = targetRayOrigin;
						}
						else
						{
							mainMenuHideData.hideFlags |= MenuHideFlags.Hidden;

							var customMenuOverridden = customMenu != null &&
								(menuHideData[customMenu].hideFlags & MenuHideFlags.OtherMenu) != 0;
							// Move alternate menu if overriding custom menu
							if (customMenuOverridden && alternateMenuVisible)
							{
								foreach (var otherDeviceData in evr.m_DeviceData)
								{
									if (deviceData == otherDeviceData)
										continue;

									if (otherDeviceData.alternateMenu != null)
										SetAlternateMenuVisibility(rayOrigin, true);
								}
							}
						}
					}
				}
			}

			static GameObject InstantiateMenuUI(Transform rayOrigin, IMenu prefab)
			{
				var ui = evr.GetNestedModule<UI>();
				GameObject go = null;
				Rays.ForEachProxyDevice(deviceData =>
				{
					var proxy = deviceData.proxy;
					var otherRayOrigin = deviceData.rayOrigin;
					if (proxy.rayOrigins.ContainsValue(rayOrigin) && otherRayOrigin != rayOrigin)
					{
						Transform menuOrigin;
						if (proxy.menuOrigins.TryGetValue(otherRayOrigin, out menuOrigin))
						{
							if (deviceData.customMenu == null)
							{
								go = ui.InstantiateUI(prefab.gameObject, menuOrigin, false);

								var customMenu = go.GetComponent<IMenu>();
								deviceData.customMenu = customMenu;
								deviceData.menuHideData[customMenu] = new MenuHideData { hideFlags = 0 };
							}
						}
					}
				});

				return go;
			}

			internal static IMainMenu SpawnMainMenu(Type type, InputDevice device, bool visible, out ActionMapInput input)
			{
				input = null;

				if (!typeof(IMainMenu).IsAssignableFrom(type))
					return null;

				var mainMenu = (IMainMenu)ObjectUtils.AddComponent(type, evr.gameObject);
				input = evr.GetModule<DeviceInputModule>().CreateActionMapInputForObject(mainMenu, device);
				evr.m_Interfaces.ConnectInterfaces(mainMenu, device);
				mainMenu.menuHideFlags = visible ? 0 : MenuHideFlags.Hidden;

				return mainMenu;
			}

			internal static IAlternateMenu SpawnAlternateMenu(Type type, InputDevice device, out ActionMapInput input)
			{
				input = null;

				if (!typeof(IAlternateMenu).IsAssignableFrom(type))
					return null;

				var alternateMenu = (IAlternateMenu)ObjectUtils.AddComponent(type, evr.gameObject);
				input = evr.GetModule<DeviceInputModule>().CreateActionMapInputForObject(alternateMenu, device);
				evr.m_Interfaces.ConnectInterfaces(alternateMenu, device);

				return alternateMenu;
			}

			internal static MainMenuActivator SpawnMainMenuActivator(InputDevice device)
			{
				var mainMenuActivator = ObjectUtils.Instantiate(evr.m_MainMenuActivatorPrefab.gameObject).GetComponent<MainMenuActivator>();
				evr.m_Interfaces.ConnectInterfaces(mainMenuActivator, device);

				return mainMenuActivator;
			}

			public static PinnedToolButton SpawnPinnedToolButton(InputDevice device)
			{
				var button = ObjectUtils.Instantiate(evr.m_PinnedToolButtonPrefab.gameObject).GetComponent<PinnedToolButton>();
				evr.m_Interfaces.ConnectInterfaces(button, device);

				return button;
			}

			internal static void UpdateAlternateMenuActions()
			{
				var actionsModule = evr.GetModule<ActionsModule>();
				foreach (var deviceData in evr.m_DeviceData)
				{
					var altMenu = deviceData.alternateMenu;
					if (altMenu != null)
						altMenu.menuActions = actionsModule.menuActions;
				}
			}

			static bool IsMainMenuVisible(Transform rayOrigin)
			{
				foreach (var deviceData in evr.m_DeviceData)
				{
					if (deviceData.rayOrigin == rayOrigin)
						return (deviceData.menuHideData[deviceData.mainMenu].hideFlags & MenuHideFlags.Hidden) == 0;
				}

				return false;
			}
		}
	}
}
#endif
