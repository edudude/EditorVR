using UnityEngine;
using System.Collections;
using UnityEngine.VR.Tools;
using System;
using UnityEngine.InputNew;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.VR.Utilities;
using UnityEditor.VR;

[MainMenuItem("Annotation", "Tools", "Draw in the space")]
public class AnnotationTool : MonoBehaviour, ITool, ICustomActionMap, IUsesRayOrigin, ICustomRay, IUsesRayOrigins, IInstantiateUI
{
	public Transform rayOrigin { private get; set; }
	public List<Transform> otherRayOrigins { private get; set; }

	public Action showDefaultRay { get; set; }
	public Action hideDefaultRay { get; set; }

	public ActionMap actionMap
	{
		get { return m_ActionMap; }
	}
	[SerializeField]
	private ActionMap m_ActionMap;

	public ActionMapInput actionMapInput { get { return m_AnnotationInput; } set { m_AnnotationInput = (AnnotationInput)value; } }

	public Func<GameObject, GameObject> instantiateUI { private get; set; }

	private AnnotationInput m_AnnotationInput;

	private const int kInitialListSize = 32767;

	private List<Vector3> m_Points = new List<Vector3>(kInitialListSize);
	private List<Vector3> m_Forwards = new List<Vector3>(kInitialListSize);
	private List<float> m_Widths = new List<float>(kInitialListSize);
	private List<Vector3> m_Rights = new List<Vector3>();

	private MeshFilter m_CurrentMeshFilter;
	private Color m_ColorToUse = Color.white;
	private Mesh m_CurrentMesh;
	private Matrix4x4 m_WorldToLocalMesh;

	[SerializeField]
	private Material m_AnnotationMaterial;

	[SerializeField]
	private Material m_ConeMaterial;
	private Material m_ConeMaterialInstance;

	[SerializeField]
	private GameObject m_ColorPickerPrefab;
	private ColorPickerUI m_ColorPicker;

	[SerializeField]
	private GameObject m_BrushSizePrefab;
	private BrushSizeUI m_BrushSizeUi;

	private Action<float> onBrushSizeChanged { set; get; }

	private Transform m_AnnotationHolder;

	private bool m_RayHidden;

	private Mesh m_CustomPointerMesh;
	private GameObject m_CustomPointerObject;

	private const float kTopMinRadius = 0.001f;
	private const float kTopMaxRadius = 0.05f;
	private const float kBottomRadius = 0.01f;
	private const float kTipDistance = 0.05f;
	private const int kSides = 16;

	private float m_CurrentRadius = kTopMinRadius;

	private List<GameObject> m_UndoList = new List<GameObject>();

	void OnDestroy()
	{
		if (m_RayHidden && showDefaultRay != null)
			showDefaultRay();

		if (m_ColorPicker)
			U.Object.Destroy(m_ColorPicker.gameObject);
		if (m_BrushSizeUi)
			U.Object.Destroy(m_BrushSizeUi.gameObject);
		if (m_CustomPointerObject)
			DestroyImmediate(m_CustomPointerObject);
	}
	
	void Update()
	{
		if (!m_RayHidden)
		{
			if (hideDefaultRay != null)
			{
				hideDefaultRay();
				m_RayHidden = true;
			}
		}

		if (rayOrigin != null)
		{
			GenerateCustomPointer();

			if (otherRayOrigins != null)
				CheckColorPicker();

			CheckBrushSizeUi();
		}

		if (m_AnnotationInput.draw.wasJustPressed)
			SetupAnnotation();
		else if (m_AnnotationInput.draw.isHeld)
			UpdateAnnotation();
		else if (m_AnnotationInput.draw.wasJustReleased)
		{
			m_CurrentMesh.Optimize();
			m_CurrentMesh.UploadMeshData(true);
		}
		else if (m_AnnotationInput.undo.wasJustPressed)
			UndoLast();

		if (m_AnnotationInput.changeBrushSize.value != 0)
			HandleBrushSize();
	}

	private void UndoLast()
	{
		if (m_UndoList.Count > 0)
		{
			var first = m_UndoList.Last();
			DestroyImmediate(first);
			m_UndoList.RemoveAt(m_UndoList.Count - 1);

			// Clean up after the removed annotations if necessary.
			if (m_AnnotationHolder.childCount == 0)
			{
				var root = m_AnnotationHolder.parent;
				int index = m_AnnotationHolder.GetSiblingIndex();
				DestroyImmediate(m_AnnotationHolder.gameObject);

				if (root.childCount == 0)
					DestroyImmediate(root.gameObject);
				else
				{
					if (index > 0)
						m_AnnotationHolder = root.GetChild(index - 1);
					else if (index < root.childCount)
						m_AnnotationHolder = root.GetChild(index);
				}
			}
		}
	}

	private void CheckBrushSizeUi()
	{
		if (m_BrushSizeUi == null)
		{
			var bsuiObj = instantiateUI(m_BrushSizePrefab);
			m_BrushSizeUi = bsuiObj.GetComponent<BrushSizeUI>();
			var trans = bsuiObj.transform;
			trans.SetParent(rayOrigin);
			trans.localPosition = m_BrushSizePrefab.transform.localPosition;
			trans.localRotation = m_BrushSizePrefab.transform.localRotation;

			m_BrushSizeUi.onValueChanged = (val) => 
			{
				m_CurrentRadius = Mathf.Lerp(kTopMinRadius, kTopMaxRadius, val);
				ResizePointer();
			};
			onBrushSizeChanged = m_BrushSizeUi.ChangeSliderValue;
		}
	}

	private void CheckColorPicker()
	{
		foreach (var otherRayOrigin in otherRayOrigins)
		{
			var otherRayMatrix = otherRayOrigin.worldToLocalMatrix;
			var rayLocalPos = otherRayMatrix.MultiplyPoint3x4(rayOrigin.position);
			var distance = Mathf.Abs(rayLocalPos.x);

			if (distance < .1625f)
			{
				if (m_ColorPicker == null)
				{
					var colorPickerObj = instantiateUI(m_ColorPickerPrefab);
					m_ColorPicker = colorPickerObj.GetComponent<ColorPickerUI>();
					m_ColorPicker.toolRayOrigin = rayOrigin;
					m_ColorPicker.onColorPicked = OnColorPickerValueChanged;

					var pickerTransform = m_ColorPicker.transform;
					pickerTransform.SetParent(otherRayOrigin);
					pickerTransform.localPosition = m_ColorPickerPrefab.transform.localPosition;
					pickerTransform.localRotation = m_ColorPickerPrefab.transform.localRotation;
				}
				else if (m_ColorPicker.transform.parent != otherRayOrigin)
				{
					var pickerTransform = m_ColorPicker.transform;
					pickerTransform.SetParent(otherRayOrigin);
					pickerTransform.localPosition = m_ColorPickerPrefab.transform.localPosition;
					pickerTransform.localRotation = m_ColorPickerPrefab.transform.localRotation;
				}

				float dot = Vector3.Dot(otherRayOrigin.right, rayOrigin.position - otherRayOrigin.position);
				Vector3 localPos = m_ColorPicker.transform.localPosition;
				localPos.x = Mathf.Abs(localPos.x) * Mathf.Sign(dot);
				m_ColorPicker.transform.localPosition = localPos;

				if (!m_ColorPicker.enabled)
					m_ColorPicker.Show();
			}
			else if (m_ColorPicker && m_ColorPicker.enabled)
				m_ColorPicker.Hide();
		}
	}

	private void OnColorPickerValueChanged(Color newColor)
	{
		m_ColorToUse = newColor;

		newColor.a = .75f;
		m_ConeMaterialInstance.SetColor("_Color", newColor);
		m_ConeMaterialInstance.SetColor("_EmissionColor", newColor);

		m_BrushSizeUi.OnBrushColorChanged(newColor);
	}

	private void HandleBrushSize()
	{
		if (m_CustomPointerMesh != null)
		{
			var sign = Mathf.Sign(m_AnnotationInput.changeBrushSize.value);
			m_CurrentRadius += sign * Time.unscaledDeltaTime * .1f;
			m_CurrentRadius = Mathf.Clamp(m_CurrentRadius, kTopMinRadius, kTopMaxRadius);

			if (m_BrushSizeUi && onBrushSizeChanged != null)
			{
				var ratio = Mathf.InverseLerp(kTopMinRadius, kTopMaxRadius, m_CurrentRadius);
				onBrushSizeChanged(ratio);
			}

			ResizePointer();
		}
	}

	private void ResizePointer()
	{
		var vertices = m_CustomPointerMesh.vertices;
		for (int i = kSides; i < kSides * 2; i++)
		{
			float angle = (i / (float)kSides) * Mathf.PI * 2f;
			float xPos = Mathf.Cos(angle) * m_CurrentRadius;
			float yPos = Mathf.Sin(angle) * m_CurrentRadius;

			Vector3 point = new Vector3(xPos, yPos, kTipDistance);
			vertices[i] = point;
		}
		m_CustomPointerMesh.vertices = vertices;
	}

	private void GenerateCustomPointer()
	{
		if (m_CustomPointerMesh != null)
			return;

		m_CustomPointerMesh = new Mesh();
		m_CustomPointerMesh.vertices = GenerateVertices();
		m_CustomPointerMesh.triangles = GenerateTriangles();

		m_CustomPointerObject = new GameObject("CustomPointer");

		m_CustomPointerObject.AddComponent<MeshFilter>().sharedMesh = m_CustomPointerMesh;
		
		m_ConeMaterialInstance = Instantiate(m_ConeMaterial);
		m_CustomPointerObject.AddComponent<MeshRenderer>().sharedMaterial = m_ConeMaterialInstance;

		var pointerTrans = m_CustomPointerObject.transform;
		pointerTrans.SetParent(rayOrigin);

		pointerTrans.localPosition = Vector3.zero;
		pointerTrans.localScale = Vector3.one;
		pointerTrans.localRotation = Quaternion.identity;
	}

	private Vector3[] GenerateVertices()
	{
		List<Vector3> points = new List<Vector3>();

		for (int capIndex = 0; capIndex < 2; capIndex++)
		{
			float radius = capIndex == 0 ? kBottomRadius : Mathf.Lerp(kTopMaxRadius, kTopMinRadius, capIndex);

			for (int i = 0; i < kSides; i++)
			{
				float angle = (i / (float)kSides) * Mathf.PI * 2f;
				float xPos = Mathf.Cos(angle) * radius;
				float yPos = Mathf.Sin(angle) * radius;

				Vector3 point = new Vector3(xPos, yPos, capIndex * kTipDistance);
				points.Add(point);
			}
		}
		points.Add(new Vector3(0, 0, 0));
		points.Add(new Vector3(0, 0, kTipDistance));

		return points.ToArray();
	}

	private int[] GenerateTriangles()
	{
		List<int> triangles = new List<int>();

		GenerateSideTriangles(triangles);
		GenerateCapsTriangles(triangles);

		return triangles.ToArray();
	}

	private void GenerateSideTriangles(List<int> triangles)
	{
		for (int i = 1; i < kSides; i++)
		{
			int lowerLeft = i - 1;
			int lowerRight = i;
			int upperLeft = i + kSides - 1;
			int upperRight = i + kSides;

			int[] sideTriangles = VerticesToPolygon(upperRight, upperLeft, lowerRight, lowerLeft, false);
			triangles.AddRange(sideTriangles);
		}

		// Finish the side with a polygon that loops around from the end to the start vertices.
		int[] finishTriangles = VerticesToPolygon(kSides, kSides * 2 - 1, 0, kSides - 1, false);
		triangles.AddRange(finishTriangles);
	}

	private void GenerateCapsTriangles(List<int> triangles)
	{
		// Generate the bottom circle cap.
		for (int i = 1; i < kSides; i++)
		{
			int lowerLeft = i - 1;
			int lowerRight = i;
			int upperLeft = kSides * 2;
			
			triangles.Add(upperLeft);
			triangles.Add(lowerRight);
			triangles.Add(lowerLeft);
		}

		// Close the bottom circle cap with a start-end loop triangle.
		triangles.Add(kSides * 2);
		triangles.Add(0);
		triangles.Add(kSides - 1);

		// Generate the top circle cap.
		for (int i = kSides + 1; i < kSides * 2; i++)
		{
			int lowerLeft = i - 1;
			int lowerRight = i;
			int upperLeft = kSides * 2 + 1;

			triangles.Add(lowerLeft);
			triangles.Add(lowerRight);
			triangles.Add(upperLeft);
		}

		// Close the top circle cap with a start-end loop triangle.
		triangles.Add(kSides * 2 - 1);
		triangles.Add(kSides);
		triangles.Add(kSides * 2 + 1);
	}

	private void SetupAnnotation()
	{
		SetupHolder();

		m_Points.Clear();
		m_Forwards.Clear();
		m_Widths.Clear();
		m_Rights.Clear();

		GameObject go = new GameObject("Annotation " + m_AnnotationHolder.childCount);
		m_UndoList.Add(go);

		Transform goTrans = go.transform;
		goTrans.SetParent(m_AnnotationHolder);
		goTrans.position = rayOrigin.position;

		m_CurrentMeshFilter = go.AddComponent<MeshFilter>();
		MeshRenderer mRenderer = go.AddComponent<MeshRenderer>();

		var matToUse = Instantiate(m_AnnotationMaterial);
		matToUse.SetColor("_EmissionColor", m_ColorToUse);
		mRenderer.sharedMaterial = matToUse;

		m_WorldToLocalMesh = goTrans.worldToLocalMatrix;

		m_CurrentMesh = new Mesh();
		m_CurrentMesh.name = "Annotation";
	}

	private void SetupHolder()
	{
		var mainHolder = GameObject.Find("Annotations") ?? new GameObject("Annotations");
		var mainHolderTrans = mainHolder.transform;

		GameObject newSession = GetNewSessionHolder(mainHolderTrans);
		if (!newSession)
			newSession = new GameObject("Group " + mainHolderTrans.childCount);

		m_AnnotationHolder = newSession.transform;
		m_AnnotationHolder.SetParent(mainHolder.transform);
	}

	private GameObject GetNewSessionHolder(Transform mainHolderTrans)
	{
		const float kGroupingDistance = .3f;
		GameObject newSession = null;

		for (int i = 0; i < mainHolderTrans.childCount; i++)
		{
			var child = mainHolderTrans.GetChild(i);
			child.name = "Group " + i;

			if (!newSession)
			{
				var renderers = child.GetComponentsInChildren<MeshRenderer>();
				if (renderers.Length > 0)
				{
					Bounds bound = renderers[0].bounds;
					for (int r = 1; r < renderers.Length; r++)
						bound.Encapsulate(renderers[r].bounds);

					if (bound.Contains(rayOrigin.position))
						newSession = child.gameObject;
					else if (bound.SqrDistance(rayOrigin.position) < kGroupingDistance)
						newSession = child.gameObject;

					if (newSession)
						break;
				}
			}
		}

		return newSession;
	}
	
	private void UpdateAnnotation()
	{
		Vector3 rayForward = rayOrigin.forward;
		Vector3 rayRight = rayOrigin.right;
		Vector3 worldPoint = rayOrigin.position + rayForward * kTipDistance;
		Vector3 localPoint = m_WorldToLocalMesh.MultiplyPoint3x4(worldPoint);

		InterpolatePointsIfNeeded(localPoint, rayForward, rayRight);
		
		m_Points.Add(localPoint);
		m_Forwards.Add(rayForward);
		m_Widths.Add(m_CurrentRadius);
		m_Rights.Add(rayRight);

		PointsToMesh();
	}

	private void InterpolatePointsIfNeeded(Vector3 localPoint, Vector3 rayForward, Vector3 rayRight)
	{
		if (m_Points.Count > 1)
		{
			var lastPoint = m_Points.Last();
			var distance = Vector3.Distance(lastPoint, localPoint);

			if (distance > m_CurrentRadius * .5f)
			{
				var halfPoint = (lastPoint + localPoint) / 2f;
				m_Points.Add(halfPoint);

				var halfForward = (m_Forwards.Last() + rayForward) / 2f;
				m_Forwards.Add(halfForward);

				var halfRadius = (m_Widths.Last() + m_CurrentRadius) / 2f;
				m_Widths.Add(halfRadius);

				var halfRight = (m_Rights.Last() + rayRight) / 2f;
				m_Rights.Add(halfRight);
			}
		}
	}
	
	private void PointsToMesh()
	{
		if (m_Points.Count < 4)
			return;

		if (m_CurrentMesh == null)
			m_CurrentMesh = new Mesh();

		List<Vector3> newVertices = new List<Vector3>();
		List<int> newTriangles = new List<int>();
		List<Vector2> newUvs = new List<Vector2>();

		LineToPlane(newVertices);
		SmoothPlane(newVertices);

		newVertices.RemoveRange(0, Mathf.Min(newVertices.Count, 4));
		if (newVertices.Count > 4)
			newVertices.RemoveRange(newVertices.Count - 4, 4);

		TriangulatePlane(newTriangles, newVertices.Count);
		CalculateUvs(newUvs, newVertices);
		
		m_CurrentMesh.Clear();

		m_CurrentMesh.vertices = newVertices.ToArray();
		m_CurrentMesh.triangles = newTriangles.ToArray();
		m_CurrentMesh.uv = newUvs.ToArray();

		m_CurrentMesh.UploadMeshData(false);

		m_CurrentMeshFilter.sharedMesh = m_CurrentMesh;
	}

	private void LineToPlane(List<Vector3> newVertices)
	{
		Vector3 prevDirection = (m_Points[1] - m_Points[0]).normalized;

		for (int i = 1; i < m_Points.Count; i++)
		{
			Vector3 nextPoint = m_Points[i];
			Vector3 thisPoint = m_Points[i - 1];
			Vector3 direction = (nextPoint - thisPoint).normalized;

			// For optimization, ignore inner points of an almost straight line.
			// The last point is an exception, it is required for a smooth drawing experience.
			if (Vector3.Angle(prevDirection, direction) < 1f && i < m_Points.Count - 1 && i > 1)
				continue;

			var ratio = Mathf.Abs(Vector3.Dot(direction, m_Forwards[i - 1]));
			var cross1 = m_Rights[i - 1].normalized;
			var cross2 = Vector3.Cross(direction, m_Forwards[i - 1]).normalized;
			var cross = Vector3.Lerp(cross1, cross2, 1 - ratio).normalized;

			float lowWidth = Mathf.Min((i - 3) * 0.1f, 1);
			float highWidth = Mathf.Min((m_Points.Count - (i + 2)) * 0.25f, 1);
			float unclampedWidth = m_Widths[i - 1] * Mathf.Clamp01(i < m_Points.Count / 2f ? lowWidth : highWidth);
			float width = Mathf.Clamp(unclampedWidth, kTopMinRadius, kTopMaxRadius);

			Vector3 left = thisPoint - cross * width;
			Vector3 right = thisPoint + cross * width;

			newVertices.Add(left);
			newVertices.Add(right);

			prevDirection = direction;
		}
	}

	private void SmoothPlane(List<Vector3> newVertices)
	{
		const float kSmoothRatio = .75f;
		for (int side = 0; side < 2; side++)
		{
			for (int i = 4; i < newVertices.Count - 4 - side; i++)
			{
				Vector3 average = (newVertices[i - 4 + side] + newVertices[i - 2 + side] + newVertices[i + 2 + side] + newVertices[i + 4 + side]) / 4f;
				float dynamicSmooth = 1 / Vector3.Distance(newVertices[i + side], average);
				newVertices[i + side] = Vector3.Lerp(newVertices[i + side], average, kSmoothRatio * dynamicSmooth);
			}
		}
	}

	private void TriangulatePlane(List<int> newTriangles, int vertexCount)
	{
		for (int i = 3; i < vertexCount; i += 2)
		{
			int upperLeft = i - 1;
			int upperRight = i;
			int lowerLeft = i - 3;
			int lowerRight = i - 2;

			int[] triangles = VerticesToPolygon(upperLeft, upperRight, lowerLeft, lowerRight);
			newTriangles.AddRange(triangles);
		}
	}
	
	private void CalculateUvs(List<Vector2> newUvs, List<Vector3> newVertices)
	{
		for (int i = 0; i < newVertices.Count; i += 2)
		{
			for (int side = 0; side < 2; side++)
				newUvs.Add(new Vector2(side, i / 2));
		}
	}

	private int[] VerticesToPolygon(int upperLeft, int upperRight, int lowerLeft, int lowerRight, bool doubleSided = true)
	{
		int triangleCount = doubleSided ? 12 : 6;
		int[] triangles = new int[triangleCount];
		int index = 0;

		triangles[index++] = upperLeft;
		triangles[index++] = lowerRight;
		triangles[index++] = lowerLeft;

		triangles[index++] = lowerRight;
		triangles[index++] = upperLeft;
		triangles[index++] = upperRight;

		if (doubleSided)
		{
			triangles[index++] = lowerLeft;
			triangles[index++] = lowerRight;
			triangles[index++] = upperLeft;

			triangles[index++] = upperRight;
			triangles[index++] = upperLeft;
			triangles[index++] = lowerRight;
		}

		return triangles;
	}

}
