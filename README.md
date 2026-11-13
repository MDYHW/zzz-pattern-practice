# zzz-pattern-practice (v1)

젠레스 존 제로 보스 패턴 연습장이다. 서비스 주소: https://zzz-pattern-practice.github.io/

- 이 저장소는 source만 가진다. push해도 site는 바뀌지 않는다.
- 배포는 저장소 `zzz-pattern-practice/zzz-pattern-practice.github.io`의 `deploy.yml`(workflow_dispatch)을 이 저장소의 40자리 SHA로 손으로 실행할 때만 일어난다. workflow는 Node 24.19.0에서 `npm ci` → `npm run check`(typecheck + test + build) → `dist`를 올린다.
- 지금 배포된 SHA는 `0825eab`(`main`)이다. 이 정리 commit은 배포되지 않았다. 같은 내용으로 다시 배포할 수 있게 `npm run check`가 읽는 파일만 남겼다.
- `docs/evidence`의 JSON 6개와 `tools/experiment/profiles`는 `src/content/skills.test.ts`가 읽어서 남아 있다.
- 지운 문서·도구·browser test·측정 근거는 git history(`0825eab` 이전)에 있다.
- 이어지는 v2는 `MDYHW/zzz-boss-practice`에서 개발한다.
