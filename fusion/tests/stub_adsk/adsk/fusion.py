# Stub of adsk.fusion: a document tree the add-in can walk.
#
# Bodies carry a box extent in CENTIMETRES, because that is what the real API
# reports, and the add-in's job is to convert. A stub that handed over
# millimetres would quietly validate the wrong behaviour.


class TriangleMesh:
    def __init__(self, coords_cm, indices):
        self.nodeCoordinatesAsFloat = coords_cm
        self.nodeIndices = indices
        self.nodeCount = len(coords_cm) // 3
        self.triangleCount = len(indices) // 3


class TriangleMeshCalculator:
    def __init__(self, body):
        self._body = body
        self.surfaceTolerance = 0.1
        self.maxSideLength = 0.0

    def calculate(self):
        if self._body.fail:
            return None
        return self._body._mesh()


class MeshManager:
    def __init__(self, body):
        self._body = body

    def createMeshCalculator(self):
        return TriangleMeshCalculator(self._body)


class BRepBody:
    """A box, in centimetres, from (x0,y0,z0) with the given size."""

    def __init__(self, name, size_cm=1.0, origin_cm=(0.0, 0.0, 0.0),
                 visible=True, revision="r1", fail=False):
        self.name = name
        self.isVisible = visible
        self.isLightBulbOn = True
        self.revisionId = revision
        self.size = float(size_cm)
        self.origin = origin_cm
        self.fail = fail
        self.meshManager = MeshManager(self)

    def _mesh(self):
        x, y, z = self.origin
        s = self.size
        coords = [
            x,     y,     z,      x + s, y,     z,      x + s, y + s, z,      x,     y + s, z,
            x,     y,     z + s,  x + s, y,     z + s,  x + s, y + s, z + s,  x,     y + s, z + s,
        ]
        idx = [
            0, 3, 2,  0, 2, 1,
            4, 5, 6,  4, 6, 7,
            0, 1, 5,  0, 5, 4,
            2, 3, 7,  2, 7, 6,
            1, 2, 6,  1, 6, 5,
            3, 0, 4,  3, 4, 7,
        ]
        return TriangleMesh([float(c) for c in coords], idx)


class Occurrence:
    def __init__(self, name, bodies, visible=True, parent_path=""):
        self.name = name
        self.isVisible = visible
        self.bRepBodies = list(bodies)
        self.fullPathName = (parent_path + "/" + name) if parent_path else name


class Component:
    def __init__(self, bodies=(), occurrences=()):
        self.bRepBodies = list(bodies)
        self.allOccurrences = list(occurrences)


class Design:
    def __init__(self, root):
        self.rootComponent = root
