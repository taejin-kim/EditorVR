#if UNITY_EDITOR && UNITY_EDITORVR
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.EditorVR.Modules;
using UnityEditor.Experimental.EditorVR.UI;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UnityEditor.Experimental.EditorVR.Core
{
	partial class EditorVR
	{
		[SerializeField]
		Camera m_EventCameraPrefab;

		class UI : Nested, IInterfaceConnector
		{
			const byte k_MinStencilRef = 2;

			byte stencilRef
			{
				get { return m_StencilRef; }
				set
				{
					m_StencilRef = (byte)Mathf.Clamp(value, k_MinStencilRef, byte.MaxValue);

					// Wrap
					if (m_StencilRef == byte.MaxValue)
						m_StencilRef = k_MinStencilRef;
				}
			}
			byte m_StencilRef = k_MinStencilRef;

			Camera m_EventCamera;

			readonly List<IManipulatorController> m_ManipulatorControllers = new List<IManipulatorController>();
			readonly HashSet<ISetManipulatorsVisible> m_ManipulatorsHiddenRequests = new HashSet<ISetManipulatorsVisible>();

			public UI()
			{
				IInstantiateUIMethods.instantiateUI = InstantiateUI;
				IRequestStencilRefMethods.requestStencilRef = RequestStencilRef;
				ISetManipulatorsVisibleMethods.setManipulatorsVisible = SetManipulatorsVisible;
				IGetManipulatorDragStateMethods.getManipulatorDragState = GetManipulatorDragState;
			}

			public void ConnectInterface(object obj, Transform rayOrigin = null)
			{
				var manipulatorController = obj as IManipulatorController;
				if (manipulatorController != null)
					m_ManipulatorControllers.Add(manipulatorController);

				var usesStencilRef = obj as IUsesStencilRef;
				if (usesStencilRef != null)
				{
					byte? stencilRef = null;

					var mb = obj as MonoBehaviour;
					if (mb)
					{
						var parent = mb.transform.parent;
						if (parent)
						{
							// For workspaces and tools, it's likely that the stencil ref should be shared internally
							var parentStencilRef = parent.GetComponentInParent<IUsesStencilRef>();
							if (parentStencilRef != null)
								stencilRef = parentStencilRef.stencilRef;
						}
					}

					usesStencilRef.stencilRef = stencilRef ?? RequestStencilRef();
				}
			}

			public void DisconnectInterface(object obj, Transform rayOrigin = null)
			{
				var manipulatorController = obj as IManipulatorController;
				if (manipulatorController != null)
					m_ManipulatorControllers.Remove(manipulatorController);
			}

			internal void Initialize()
			{
				// Create event system, input module, and event camera
				ObjectUtils.AddComponent<EventSystem>(evr.gameObject);

				var inputModule = evr.AddModule<MultipleRayInputModule>();

				var customPreviewCamera = evr.GetNestedModule<Viewer>().customPreviewCamera;
				if (customPreviewCamera != null)
					inputModule.layerMask |= customPreviewCamera.hmdOnlyLayerMask;

				m_EventCamera = ObjectUtils.Instantiate(evr.m_EventCameraPrefab.gameObject, evr.transform).GetComponent<Camera>();
				m_EventCamera.enabled = false;
				inputModule.eventCamera = m_EventCamera;

				inputModule.preProcessRaycastSource = evr.GetNestedModule<Rays>().PreProcessRaycastSource;
			}

			internal GameObject InstantiateUI(GameObject prefab, Transform parent = null, bool worldPositionStays = true)
			{
				var go = ObjectUtils.Instantiate(prefab, parent ? parent : evr.transform, worldPositionStays);
				foreach (var canvas in go.GetComponentsInChildren<Canvas>())
					canvas.worldCamera = m_EventCamera;

				var keyboardModule = evr.GetModule<KeyboardModule>();
				foreach (var inputField in go.GetComponentsInChildren<InputField>())
				{
					if (inputField is NumericInputField)
						inputField.spawnKeyboard = keyboardModule.SpawnNumericKeyboard;
					else if (inputField is StandardInputField)
						inputField.spawnKeyboard = keyboardModule.SpawnAlphaNumericKeyboard;
				}

				foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
					evr.m_Interfaces.ConnectInterfaces(mb);

				return go;
			}

			void SetManipulatorsVisible(ISetManipulatorsVisible setter, bool visible)
			{
				if (visible)
					m_ManipulatorsHiddenRequests.Remove(setter);
				else
					m_ManipulatorsHiddenRequests.Add(setter);
			}

			bool GetManipulatorDragState()
			{
				return m_ManipulatorControllers.Any(controller => controller.manipulatorDragging);
			}

			internal void UpdateManipulatorVisibilites()
			{
				var manipulatorsVisible = m_ManipulatorsHiddenRequests.Count == 0;
				foreach (var controller in m_ManipulatorControllers)
				{
					controller.manipulatorVisible = manipulatorsVisible;
				}
			}

			byte RequestStencilRef()
			{
				return stencilRef++;
			}
		}
	}
}
#endif
