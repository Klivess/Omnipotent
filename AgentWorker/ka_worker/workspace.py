"""Linux-authoritative workspaces; chunked transfer with optimistic version checks."""
import base64
import hashlib
import os
from pathlib import Path
import stat
import threading
import shutil

from .state import identifier


class Workspace:
    def __init__(self, root):
        self.root = Path(root)
        self.lock = threading.Lock()

    def directory(self, project):
        directory = self.root / identifier(project)
        if not directory.is_dir() or directory.is_symlink():
            raise KeyError(project)
        return directory

    @staticmethod
    def parts(path):
        if not isinstance(path, str) or path.startswith(("/", "\\")) or "\\" in path or ":" in path:
            raise ValueError("Use a project-relative path")
        parts = path.split("/") if path else []
        if any(part in ("", ".", "..") or "\0" in part for part in parts):
            raise ValueError("Invalid project-relative path")
        return parts

    def parent(self, project, path):
        parts = self.parts(path)
        if not parts:
            raise ValueError("File path is required")
        fd = os.open(self.directory(project), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            for part in parts[:-1]:
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = child
            return fd, parts[-1]
        except Exception:
            os.close(fd)
            raise

    @staticmethod
    def version(stream):
        stream.seek(0)
        digest = hashlib.sha256()
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(data)
        return digest.hexdigest()

    def list(self, project, path=""):
        parts = self.parts(path)
        fd = os.open(self.directory(project), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            for part in parts:
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = child
            result = []
            for name in sorted(os.listdir(fd)):
                info = os.stat(name, dir_fd=fd, follow_symlinks=False)
                result.append({"path": "/".join(parts + [name]), "directory": stat.S_ISDIR(info.st_mode),
                               "symlink": stat.S_ISLNK(info.st_mode), "bytes": info.st_size, "modified": info.st_mtime})
            return result
        finally:
            os.close(fd)

    def read(self, project, path, offset=0, count=1024 * 1024):
        fd, name = self.parent(project, path)
        try:
            with os.fdopen(os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd), "rb") as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                    raise ValueError("Only regular files can be transferred")
                version = self.version(stream)
                stream.seek(max(0, int(offset)))
                data = stream.read(min(4 * 1024 * 1024, max(1, int(count))))
                return {"data": base64.b64encode(data).decode(), "version": version,
                        "offset": stream.tell(), "bytes": os.fstat(stream.fileno()).st_size}
        finally:
            os.close(fd)

    def mutate(self, project, operation, path, destination=None, recursive=False):
        with self.lock:
            if operation == "mkdir":
                fd = os.open(self.directory(project), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    for part in self.parts(path):
                        try:
                            os.mkdir(part, 0o755, dir_fd=fd)
                            os.chown(part, 1000, 1000, dir_fd=fd, follow_symlinks=False)
                        except FileExistsError:
                            pass
                        child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                        os.close(fd)
                        fd = child
                    os.fsync(fd)
                finally:
                    os.close(fd)
                return
            fd, name = self.parent(project, path)
            try:
                info = os.stat(name, dir_fd=fd, follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode):
                    raise ValueError("Symlinks are not managed through the workspace API")
                if operation == "delete":
                    if stat.S_ISDIR(info.st_mode):
                        if recursive:
                            if not shutil.rmtree.avoids_symlink_attacks:
                                raise RuntimeError("Safe recursive deletion unavailable")
                            shutil.rmtree(name, dir_fd=fd)
                        else:
                            os.rmdir(name, dir_fd=fd)
                    else:
                        os.unlink(name, dir_fd=fd)
                elif operation in ("move", "copy"):
                    target_fd, target = self.parent(project, destination)
                    try:
                        try:
                            os.stat(target, dir_fd=target_fd, follow_symlinks=False)
                        except FileNotFoundError:
                            pass
                        else:
                            raise ValueError("Destination already exists")
                        if operation == "move":
                            os.rename(name, target, src_dir_fd=fd, dst_dir_fd=target_fd)
                        else:
                            if not stat.S_ISREG(info.st_mode):
                                raise ValueError("Use the computer terminal to copy directories")
                            with os.fdopen(os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=fd), "rb") as source, os.fdopen(os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=target_fd), "wb") as output:
                                shutil.copyfileobj(source, output, 1024 * 1024)
                                output.flush()
                                os.fsync(output.fileno())
                                os.fchown(output.fileno(), 1000, 1000)
                        os.fsync(target_fd)
                    finally:
                        os.close(target_fd)
                else:
                    raise ValueError("Unsupported workspace operation")
                os.fsync(fd)
            finally:
                os.close(fd)

    def write(self, project, path, body):
        # Transfers stage outside agent-writable trees; final replacement uses an anchored dirfd.
        transfer = identifier(body["transferID"])
        stage_root = self.root.parent / "transfers" / identifier(project)
        stage_root.mkdir(parents=True, exist_ok=True, mode=0o700)
        stage = stage_root / transfer
        data = base64.b64decode(body.get("data", ""), validate=True)
        if len(data) > 4 * 1024 * 1024:
            raise ValueError("Chunk exceeds 4 MiB")
        offset = int(body.get("offset", 0))
        with self.lock:
            if stage.exists() and stage.stat().st_size > offset:
                with stage.open("rb") as stream:
                    stream.seek(offset)
                    if stream.read(len(data)) != data:
                        raise ValueError("Retry does not match the previously staged bytes")
            else:
                actual = stage.stat().st_size if stage.exists() else 0
                if actual != offset:
                    raise ValueError("Resume offset mismatch")
                with stage.open("ab") as stream:
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
            if not body.get("commit"):
                return {"offset": stage.stat().st_size}
            if "expectedVersion" not in body:
                raise ValueError("expectedVersion is required; use null only for a new file")
            with stage.open("rb") as stream:
                staged_hash = self.version(stream)
            if staged_hash != body.get("sha256"):
                raise ValueError("Staged SHA-256 mismatch")
            fd, name = self.parent(project, path)
            temp = ".ka-import-" + transfer
            try:
                current = None
                try:
                    with os.fdopen(os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd), "rb") as stream:
                        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                            raise ValueError("Destination is not a regular file")
                        current = self.version(stream)
                except FileNotFoundError:
                    pass
                if current == staged_hash:
                    return {"offset": stage.stat().st_size, "version": staged_hash, "committed": True}
                if current != body["expectedVersion"]:
                    raise ValueError("Workspace changed since export; import refused")
                # Caller must hold the project's migration/write lease for host-side imports.
                with os.fdopen(os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=fd), "wb") as output, stage.open("rb") as source:
                    for chunk in iter(lambda: source.read(1024 * 1024), b""):
                        output.write(chunk)
                    output.flush()
                    os.fsync(output.fileno())
                    os.fchown(output.fileno(), 1000, 1000)
                os.replace(temp, name, src_dir_fd=fd, dst_dir_fd=fd)
                os.fsync(fd)
                return {"offset": stage.stat().st_size, "version": staged_hash, "committed": True}
            finally:
                try:
                    os.unlink(temp, dir_fd=fd)
                except FileNotFoundError:
                    pass
                os.close(fd)
