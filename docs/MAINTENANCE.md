# 维护说明（fork）

本仓库是 [Sitoi/ClashBar](https://github.com/Sitoi/ClashBar) 的 fork，通过
`mh567/ClashBar` 发布自己的构建。本文说明分支模型、上游同步与分家流程。

## 分支模型

```
upstream/main ──► main（上游 + 可回流修复）──► fork/*（本仓库特有改动）
                  origin/main
```

- **`main`**：跟踪上游的可回流修复集合。上面只放上游也会认可的改动（bug 修复、测试、
  工程改进），以便随时回到「上游 + 小补丁」的状态。
- **`fork/*`**：与上游必然冲突的改动（仓库标识、发布流程、特有功能）。这些不合并回
  `main`，因为每次同步上游都要在本文件里重放冲突。
- **`origin/main`**：对外发布分支，始终等于本地 `main`。

这个划分的目的是让 `main` 与 `upstream/main` 的差异始终很小。差异越小，同步越便宜。

## 同步上游

```sh
git fetch upstream
Scripts/sync_upstream.sh --check   # 只看差异，不改动
Scripts/sync_upstream.sh           # 快进或 rebase 到 upstream/main
git push origin main
```

脚本行为：

- `main` 没有本地提交时用 `--ff-only` 快进，历史保持线性。
- `main` 有本地提交时改用 `rebase`，把你的修复重放到上游最新之上。
- rebase 遇到 `README.md` 冲突时保留 `main` 的版本，其他文件的冲突仍会中断，交给人工处理。
- 有未提交改动或不在 `main` 上时直接拒绝，避免半途状态。

`git pull upstream` 不要用。`pull` 会引入 merge 提交，让后续 rebase 变复杂。

同步后务必跑 `make test`。上游改动与你补丁的交互只能靠测试发现。

## 修上游 bug 的流程

1. 从 `main` 开分支，写修复和回归测试。
2. 合回 `main`，同步时靠 rebase 保持在上游之上。
3. 上游合入等效修复后，本地提交会在 rebase 时变成空提交，`git rebase` 会自动丢弃，
   `main` 自动回到纯上游状态。这是这套模型最大的好处。

## 本仓库特有改动（`fork/*`）

与上游必然冲突的改动只存在于 `fork/*` 分支，不合入 `main`。这类改动通常包括：
仓库标识与更新检查地址、文档站链接、以及发布流程中仅上游有权执行的步骤。

`README.md` 是唯一的例外：GitHub 首页读取默认分支 `main` 的内容，所以 `main` 上保留
一份 fork 版 README，由 `Scripts/sync_upstream.sh` 在同步时守护。修改 README 时先改
`fork/personal`，再把它复制到 `main`，两边保持一致。

具体清单见对应 `fork/*` 分支的提交说明，`main` 上不维护该清单，以免与上游冲突。

## 发版

`release-dmg.yml` 由 tag 触发：

```sh
git tag v0.4.0
git push origin v0.4.0
```

注意 tag 空间与上游共享。上游已经用到 `v0.3.6`，本地 tag 一旦与上游重名，
`git push` 会被拒绝，或者拉取上游 tag 时产生歧义。发版前先确认：

```sh
git fetch upstream --tags
git tag -l 'v0.4.*'
```

如果 fork 与上游会长期并存并发版，建议改用独立前缀（如 `fork-v0.4.0`）并在
`prepare-version` 里相应调整解析规则。

## 分家（不再跟随上游）

```sh
git branch archive/upstream-merged main   # 留个记号
git remote remove upstream
```

之后 `main` 成为唯一主线，`fork/*` 的内容按需合入。历史不需要重写。

若届时仍要发布，需要确认 `AppReleaseSource` 与文档链接已全部指向本仓库，
否则用户会被引导去安装上游版本。

## 提交身份

仓库未固定 `user.name` / `user.email`，提交会使用主机名推断的身份。若要规范：

```sh
git config user.name "你的名字"
git config user.email "你的 GitHub 邮箱"
```
