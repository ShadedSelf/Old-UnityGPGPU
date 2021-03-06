using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public struct Particle
{
	public Vector3 pos;
	public Vector3 vel;
	public float mass;
	public int flag;
}

public struct CubeData
{
	public Vector4 pos;
	public Vector4 scale;
	public Matrix4x4 rot;
}

public class Particles : MonoBehaviour 
{

	public Mesh mesh;
	public Shader shader;
	public ComputeShader particleCs;
	public ComputeShader sortcs;
	[Header("^^")]
	public int particleCount = 100000;
	public int iterations = 1;
	public float radius = .1f;
	public bool draw = true;
	public bool compute = true;
	public bool sort = true;
	[Header("World")]
	public float maxVelocity = .5f;
	public Vector3 worldSize;
	public Vector3 gravity;
	[Header("Parts")]
	public float rad = .3f;
	public float lambad = 1;
	public float rest = 100;
	public float mK = .001f;
	public float mQ = .1f;
	public float mE = 4;
	[Header("Colliders")]
	public GameObject[] cubes;

	private Material material;
	private ComputeSystem system;
	private CommandBuffer cmdBuff;
	private ComputeBuffer drawArgs;
	private Sorter sorter;

	private Vector3Int gridSize;
	int NextMultipleOf(int value, int x) => ((value / x) + 1) * x;
	private const int nn = 128;

	void OnEnable() 
	{
		material = new Material(shader);
		particleCount = Mathf.NextPowerOfTwo(particleCount);
		gridSize = new Vector3Int((int)((worldSize.x * 2) / rad), (int)((worldSize.y * 2) / rad), (int)((worldSize.z * 2) / rad));

		sorter = new Sorter(particleCount, sortcs);
		system = new ComputeSystem(particleCs, true);

		system.data.AddBuffer("tmp",			particleCount, sizeof(float) * 4);
		system.data.AddBuffer("particles",		particleCount, sizeof(float) * 4 * 2);
		system.data.AddBuffer("np",				particleCount, sizeof(float) * 4);
		system.data.AddBuffer("swapBuffer",		particleCount, sizeof(float) * 4 * 3);
		system.data.AddBuffer("collisionBuffer",particleCount, sizeof(uint) * 2);
		system.data.AddBuffer("neisBuffer",		particleCount, sizeof(uint) * (nn + 1));
		system.data.AddBuffer("cubes",			cubes.Length, sizeof(float) * 4 * 6);
		system.data.AddBuffer("color",			particleCount, sizeof(float) * 4);
		system.data.AddBuffer("curls",			particleCount, sizeof(float) * 4);
		system.data.AddBuffer("test",			particleCount, sizeof(float) * 4 * nn); //
		system.data.AddBuffer("gridBuffer",		NextMultipleOf(gridSize.x * gridSize.y * gridSize.z, 32), sizeof(uint) * 2);
		system.data.AddBuffer("cellCount",		NextMultipleOf(gridSize.x * gridSize.y * gridSize.z, 32), sizeof(uint));
		system.data.AddBuffer("nIndices",		NextMultipleOf(gridSize.x * gridSize.y * gridSize.z, 32), sizeof(uint) * nn);

		system.AddKernel("Particler",	new Vector3Int(particleCount, 1, 1));
		system.AddKernel("Swap",		new Vector3Int(particleCount, 1, 1));
		system.AddKernel("Find",		new Vector3Int(particleCount, 1, 1));
		system.AddKernel("Lamb",		new Vector3Int(particleCount, 1, 1));
		system.AddKernel("Collision",	new Vector3Int(particleCount, 1, 1));
		system.AddKernel("ApplyDelta",	new Vector3Int(particleCount, 1, 1));
		system.AddKernel("Update",		new Vector3Int(particleCount, 1, 1));
		system.AddKernel("Curl",		new Vector3Int(particleCount, 1, 1));
		system.AddKernel("Clear",		new Vector3Int(NextMultipleOf(gridSize.x * gridSize.y * gridSize.z, 32), 1, 1));

		system.BindComputeData();
		// material.SetBuffer("particles",	system.data.buffers["particles"]);
        material.SetBuffer("np",		system.data.buffers["np"]);
        material.SetBuffer("color", 	system.data.buffers["color"]);
		sorter.shader.SetBuffer(sorter.sortKernel, "collisionBuffer", system.data.buffers["collisionBuffer"]);
		

		//------------------------------
		cmdBuff = new CommandBuffer();
		cmdBuff.name = "Particle System";

		system.RecordDispatch("Particler", cmdBuff);
		if (sort)
			sorter.SetCmdBuffer(cmdBuff);
		system.RecordDispatch("Swap", cmdBuff);
		system.RecordDispatch("Find", cmdBuff);
		for (int i = 0; i < iterations; i++)
		{
			system.RecordDispatch("Lamb", cmdBuff);
			cmdBuff.SetComputeIntParam(system.shader, "currentIteration", i + 1);
			system.RecordDispatch("Collision", cmdBuff);
			system.RecordDispatch("ApplyDelta", cmdBuff);
		}
		system.RecordDispatch("Update", cmdBuff);
		system.RecordDispatch("Curl", cmdBuff);
		system.RecordDispatch("Clear", cmdBuff);

		SetData();
	}

	void SetData()
	{
		Vector4[] newPositions = new Vector4[particleCount];
        for (int i = 0; i < particleCount; i++)
		{
            newPositions[i] = Random.insideUnitSphere * 3f - new Vector3(0, 0, 2);
			// newPositions[i].w = 0.001f;
		}
		system.data.buffers["np"].SetData(newPositions);

		Particle[] particles = new Particle[particleCount];
        for (int i = 0; i < particleCount; i++)
		{
            particles[i].pos = newPositions[i];
            particles[i].vel = Vector3.zero;
            particles[i].mass = 1;
            particles[i].flag = Random.Range(0, 2);
		}
		system.data.buffers["particles"].SetData(particles);

		drawArgs = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
		uint numIndices = mesh.GetIndexCount(0);
		uint[] args = new uint[5] { numIndices, (uint)particleCount, 0, 0, 0 };
		drawArgs.SetData(args);

		cubeData = new CubeData[cubes.Length];
	}

	private CubeData[] cubeData;
	void DynamicBuffers()
	{
		for (int i = 0; i < cubes.Length; i++)
		{
			cubeData[i].pos = cubes[i].transform.position - transform.position;
			cubeData[i].scale = cubes[i].transform.lossyScale / 2;
			cubeData[i].rot = Matrix4x4.Rotate(cubes[i].transform.rotation).inverse;
		}
		system.data.buffers["cubes"].SetData(cubeData);
	}
	
	/*-- Move to FixedUpdate -- */
	void Update() 
	{
		system.shader.SetFloat("deltaTime", Time.deltaTime);
		system.shader.SetFloat("time", Time.time);
		system.shader.SetFloat("frame", Time.frameCount);
		system.shader.SetFloat("particleRad", rad);
		system.shader.SetFloat("rest", rest);
		system.shader.SetFloat("maxVelocity", maxVelocity);
		system.shader.SetFloat("radius", radius);
		system.shader.SetFloat("ep", lambad);
		system.shader.SetFloat("mQ", mQ);
		system.shader.SetFloat("mK", mK);
		system.shader.SetFloat("mE", mE);

		system.shader.SetInt("iterations", iterations);
		system.shader.SetInt("cubeCount", cubes.Length);
		system.shader.SetInt("particleCount", particleCount);
        system.shader.SetInts("gridSize", new int[3] { gridSize.x, gridSize.y, gridSize.z });

        system.shader.SetVector("gravity", gravity);
		system.shader.SetVector("worldSize", worldSize);

		material.SetVector("_WorldPos", new Vector4(transform.position.x, transform.position.y, transform.position.z));
		material.SetFloat("radius", radius);
		
		DynamicBuffers();

		if (compute) { Graphics.ExecuteCommandBuffer(cmdBuff); }
		if (draw)	 { Graphics.DrawMeshInstancedIndirect(mesh, 0, material, new Bounds(Vector3.zero, Vector3.one * 100), drawArgs); }
	}

	void OnDisable()
	{
		system.Cleanup();
		drawArgs.Release();
		cmdBuff.Release();
	}

	void OnDrawGizmos()
	{
		Gizmos.color = Color.white;
		Gizmos.DrawWireCube(transform.position, worldSize * 2/* + Vector3.one * radius * 2*/);

		// for	(int i = 0; i < gridSize; i++)
		// for	(int j = 0; j < gridSize; j++)
		// for	(int k = 0; k < gridSize; k++)
		// 	Gizmos.DrawWireCube(transform.position - worldSize + new Vector3(i, j, k) * rad + (Vector3.one * rad), Vector3.one * rad * 2);
	}
}